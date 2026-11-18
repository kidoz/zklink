using Microsoft.Extensions.Hosting;

namespace ZkLink.Extensions.DependencyInjection;

internal sealed class ZkLinkConnectionHost(
    Func<ZooKeeperClientOptions> optionsFactory,
    IZkLinkConnectionFactory connectionFactory,
    ZkLinkTerminalPolicy terminalPolicy = ZkLinkTerminalPolicy.Fail,
    IHostApplicationLifetime? application = null
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
    private Task _monitor = Task.CompletedTask;

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
                _monitor = MonitorAsync(connection, _shutdown.Token);
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
    private async Task MonitorAsync(IZkLinkConnection current, CancellationToken token)
    {
        try
        {
            while (true)
            {
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
                    application!.StopApplication();
                    return;
                }
                var next = await connectionFactory
                    .ConnectAsync(optionsFactory(), token)
                    .ConfigureAwait(false);
                Volatile.Write(ref _connection, next);
                await current.DisposeAsync().ConfigureAwait(false);
                current = next;
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception)
        {
            // Replacement failed. Leave the terminal session visible for health reporting.
            if (!token.IsCancellationRequested)
            {
                application!.StopApplication();
            }
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _lifetimeGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            if (_state is LifetimeState.Stopped or LifetimeState.Disposed)
            {
                return;
            }

            await _shutdown.CancelAsync().ConfigureAwait(false);
            await _monitor.ConfigureAwait(false);
            var connection = Interlocked.Exchange(ref _connection, null);
            _state = LifetimeState.Stopped;
            if (connection is not null)
            {
                try
                {
                    await connection.CloseAsync(cancellationToken).ConfigureAwait(false);
                }
                finally
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

    public async ValueTask DisposeAsync()
    {
        await _lifetimeGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_state == LifetimeState.Disposed)
            {
                return;
            }

            await _shutdown.CancelAsync().ConfigureAwait(false);
            await _monitor.ConfigureAwait(false);
            var connection = Interlocked.Exchange(ref _connection, null);
            _state = LifetimeState.Disposed;
            _shutdown.Dispose();
            if (connection is not null)
            {
                await connection.DisposeAsync().ConfigureAwait(false);
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
