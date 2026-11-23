using Microsoft.Extensions.Hosting;
using ZkLink.Sessions;

namespace ZkLink.Extensions.DependencyInjection;

internal sealed class ZkLinkConnectionHost(
    Func<ZooKeeperClientOptions> optionsFactory,
    IZkLinkConnectionFactory connectionFactory,
    ZkLinkTerminalPolicy terminalPolicy = ZkLinkTerminalPolicy.Fail,
    IHostApplicationLifetime? application = null,
    TimeProvider? timeProvider = null
) : IHostedService, IAsyncDisposable
{
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Reliability",
        "CA2213",
        Justification = "SemaphoreSlim needs disposal only after AvailableWaitHandle is used; retaining it keeps concurrent and repeated disposal safe."
    )]
    private readonly SemaphoreSlim _lifetimeGate = new(1, 1);
    private IZkLinkConnection? _connection;
    private LifetimeState _state;
    private readonly CancellationTokenSource _shutdown = new();
    private Task _monitorFinished = Task.CompletedTask;

    public ZooKeeperClient Client =>
        (
            Volatile.Read(ref _connection)
            ?? throw new InvalidOperationException(
                "The ZkLink client is available only after host startup and before host shutdown."
            )
        ).Client;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await _lifetimeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_state != LifetimeState.Created)
            {
                throw new InvalidOperationException(
                    "The ZkLink host lifetime has already started."
                );
            }

            var connection = await connectionFactory
                .ConnectAsync(optionsFactory(), cancellationToken)
                .ConfigureAwait(false);
            Volatile.Write(ref _connection, connection);
            _state = LifetimeState.Started;
            if (terminalPolicy != ZkLinkTerminalPolicy.Fail)
            {
                var finished = new TaskCompletionSource(
                    TaskCreationOptions.RunContinuationsAsynchronously
                );
                _monitorFinished = finished.Task;
                _ = MonitorAsync(connection, finished, _shutdown.Token);
            }
        }
        finally
        {
            _lifetimeGate.Release();
        }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Design",
        "CA1031",
        Justification = "Application options factories may throw arbitrary exceptions. A failed replacement requests host shutdown without logging credentials."
    )]
    private async Task MonitorAsync(
        IZkLinkConnection current,
        TaskCompletionSource finished,
        CancellationToken token
    )
    {
        var time = timeProvider ?? TimeProvider.System;
        var random = new SessionRandom(Random.Shared);
        var replacementRounds = 0;
        try
        {
            while (true)
            {
                var started = time.GetTimestamp();
                await current
                    .Completion.WaitAsync(token)
                    .ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
                token.ThrowIfCancellationRequested();
                if (
                    current.State
                    is not (
                        ZooKeeperSessionState.Expired
                        or ZooKeeperSessionState.Lost
                        or ZooKeeperSessionState.AuthFailed
                        or ZooKeeperSessionState.Faulted
                    )
                )
                {
                    return;
                }
                if (terminalPolicy == ZkLinkTerminalPolicy.StopApplication)
                {
                    break;
                }
                if (time.GetElapsedTime(started) >= SessionRandom.MaxBackoff)
                {
                    replacementRounds = 0;
                }
                var delay = random.Backoff(replacementRounds, SessionRandom.MaxBackoff);
                replacementRounds = Math.Min(
                    replacementRounds + 1,
                    SessionRandom.MaxBackoffExponent
                );
                await Task.Delay(delay, time, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                var next = await connectionFactory
                    .ConnectAsync(optionsFactory(), token)
                    .ConfigureAwait(false);
                // Shutdown claims the published connection before it waits for this monitor.
                if (
                    !ReferenceEquals(
                        Interlocked.CompareExchange(ref _connection, next, current),
                        current
                    )
                )
                {
                    await next.DisposeAsync().ConfigureAwait(false);
                    return;
                }
                await current.DisposeAsync().ConfigureAwait(false);
                current = next;
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            return;
        }
        catch (Exception)
        {
            // Replacement failed. Leave the terminal session visible for health reporting.
        }
        finally
        {
            // No connection work follows. A host stopping callback can run StopAsync
            // or dispose the container synchronously without joining its own monitor.
            finished.TrySetResult();
        }

        if (!token.IsCancellationRequested)
        {
            try
            {
                application!.StopApplication();
            }
            catch (Exception)
            {
                System.Diagnostics.Trace.TraceError(
                    "The application lifetime could not signal host shutdown after a terminal ZooKeeper session."
                );
            }
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) =>
        ShutdownAsync(graceful: true, cancellationToken);

    public ValueTask DisposeAsync() => new(ShutdownAsync(graceful: false, CancellationToken.None));

    private async Task ShutdownAsync(bool graceful, CancellationToken cancellationToken)
    {
        await _lifetimeGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            if (_state == LifetimeState.Disposed || (graceful && _state == LifetimeState.Stopped))
            {
                return;
            }

            var connection = Interlocked.Exchange(ref _connection, null);
            _state = graceful ? LifetimeState.Stopped : LifetimeState.Disposed;
            try
            {
                await _shutdown.CancelAsync().ConfigureAwait(false);
                await _monitorFinished.ConfigureAwait(false);
                if (graceful && connection is not null)
                {
                    try
                    {
                        await connection.CloseAsync(cancellationToken).ConfigureAwait(false);
                    }
                    catch (ZooKeeperCloseException error)
                    {
                        // Local cleanup is complete. The server did not acknowledge the
                        // close, so ephemerals may remain until it expires the session.
                        System.Diagnostics.Trace.TraceWarning(
                            FormattableString.Invariant(
                                $"The ZkLink session was released without server acknowledgment during host shutdown ({error.Outcome})."
                            )
                        );
                    }
                }
            }
            finally
            {
                if (!graceful)
                {
                    _shutdown.Dispose();
                }
                if (connection is not null)
                {
                    await connection.DisposeAsync().ConfigureAwait(false);
                }
            }
        }
        finally
        {
            _lifetimeGate.Release();
        }
    }

    private enum LifetimeState
    {
        Created,
        Started,
        Stopped,
        Disposed,
    }
}
