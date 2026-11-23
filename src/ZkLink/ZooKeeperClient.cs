using ZkLink.Configuration;
using ZkLink.Diagnostics;
using ZkLink.Protocol;
using ZkLink.Sessions;
using ZkLink.Transport;

namespace ZkLink;

/// <summary>One explicitly owned ZooKeeper session with bounded asynchronous I/O and no operation replay.</summary>
public sealed partial class ZooKeeperClient : IAsyncDisposable
{
    private readonly SessionManager _session;
    private readonly bool _enableTtlNodes;
    private readonly bool _ownsSession;

    private ZooKeeperClient(SessionManager session, bool enableTtlNodes, bool ownsSession = true)
    {
        _session = session;
        _enableTtlNodes = enableTtlNodes;
        _ownsSession = ownsSession;
    }

    internal ZooKeeperClient CreateNonOwningView() =>
        new(_session, _enableTtlNodes, ownsSession: false);

    /// <summary>Snapshots options before returning its Task, then connects/authenticates within the initial deadline.</summary>
    /// <exception cref="ArgumentException">The options are invalid, for example an unsafe plaintext digest endpoint or a client certificate without a private key.</exception>
    /// <exception cref="ZooKeeperConnectionException">The session did not become ready within the initial deadline, or the server rejected the handshake or authentication. <see cref="ZooKeeperConnectionException.Failure"/> classifies the cause.</exception>
    /// <exception cref="OperationCanceledException">The caller canceled before the session was ready.</exception>
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Reliability",
        "CA2000",
        Justification = "The asynchronous connection helper assumes snapshot ownership and disposes it on every failed construction/connect path."
    )]
    public static Task<ZooKeeperClient> ConnectAsync(
        ZooKeeperClientOptions options,
        CancellationToken cancellationToken = default
    )
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled<ZooKeeperClient>(cancellationToken);
        }
        var snapshot = ClientOptionsSnapshot.Capture(options);
        var random = new SessionRandom(Random.Shared);
        return ConnectCoreAsync(
            snapshot,
            TimeProvider.System,
            new SocketTransportConnector(new HostResolver(), random),
            random,
            cancellationToken
        );
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Reliability",
        "CA2000",
        Justification = "SessionManager transfers into ZooKeeperClient on success; the finally block awaits abort/disposal on all failures."
    )]
    internal static async Task<ZooKeeperClient> ConnectCoreAsync(
        ClientOptionsSnapshot options,
        TimeProvider time,
        ITransportConnector connector,
        SessionRandom random,
        CancellationToken cancellationToken
    )
    {
        var span = ZooKeeperTelemetry.Begin(time, TelemetryKind.Connect);
        Exception? failure = null;
        SessionManager? manager = null;
        var transferred = false;
        try
        {
            manager = new SessionManager(options, time, connector, random);
            await manager.Start().WaitAsync(cancellationToken).ConfigureAwait(false);
            var result = new ZooKeeperClient(manager, options.Settings.EnableTtlNodes);
            manager = null;
            transferred = true;
            return result;
        }
        catch (Exception error)
        {
            failure = cancellationToken.IsCancellationRequested
                ? new OperationCanceledException(cancellationToken)
                : error;
            cancellationToken.ThrowIfCancellationRequested();
            throw;
        }
        finally
        {
            if (manager is not null)
            {
                await manager.AbortInitialAsync().ConfigureAwait(false);
                await manager.DisposeAsync().ConfigureAwait(false);
            }
            else if (!transferred)
            {
                options.Dispose();
            }
            span.Complete(failure);
        }
    }

    /// <summary>Gets current immutable state without session credentials.</summary>
    public ZooKeeperSessionSnapshot Session => _session.Session;

    /// <summary>Atomically subscribes to an initial snapshot and ordered transitions. A full subscription queue completes with ZooKeeperEventOverflowException.</summary>
    /// <exception cref="ZooKeeperEventOverflowException">The number of active subscriptions is at <c>MaxBufferedEvents</c>.</exception>
    /// <exception cref="ZooKeeperSessionException">The session already ended.</exception>
    public ZooKeeperStateSubscription SubscribeStateChanges() => _session.Subscribe();

    /// <summary>Waits explicitly for readiness or fails on terminal loss; it never creates a replacement session.</summary>
    /// <exception cref="ZooKeeperSessionException">The session reached a terminal state before it was ready.</exception>
    /// <exception cref="ZooKeeperConnectionException">An initial connection failure is retained as the terminal cause.</exception>
    /// <exception cref="OperationCanceledException">The caller canceled the wait.</exception>
    public Task WaitForConnectionAsync(CancellationToken cancellationToken = default) =>
        _session.WaitForConnectionAsync(cancellationToken);

    /// <summary>Updates future endpoint attempts atomically without replacing identity, chroot or TLS policy.</summary>
    /// <exception cref="ArgumentException">The list is empty, too large, holds an invalid endpoint, or is unsafe for the configured plaintext digest.</exception>
    /// <exception cref="ZooKeeperSessionException">The session is closing or already ended.</exception>
    /// <exception cref="OperationCanceledException">The token was already canceled.</exception>
    public Task UpdateEndpointsAsync(
        IReadOnlyList<ZooKeeperEndpoint> endpoints,
        CancellationToken cancellationToken = default
    )
    {
        _session.UpdateEndpoints(endpoints, cancellationToken);
        return Task.CompletedTask;
    }

    /// <summary>Synchronizes a path using the current session without implying a globally fresh snapshot.</summary>
    /// <exception cref="ArgumentException">An argument is invalid, for example a relative path.</exception>
    /// <exception cref="System.IO.InvalidDataException">The encoded request exceeds a configured data, collection or frame limit; nothing was sent.</exception>
    /// <exception cref="ZooKeeperException">The request was not sent, was rejected by the server or has an unknown outcome; <see cref="ZooKeeperException.Outcome"/> says which. Local capacity, deadline and protocol failures use the derived types.</exception>
    /// <exception cref="ZooKeeperOperationCanceledException">The caller canceled the wait. The request outcome is preserved.</exception>
    public Task SyncAsync(string path, CancellationToken cancellationToken = default) =>
        SyncCoreAsync(
            _session.Submit(
                new PathRequest(ZooKeeperOperation.Sync, path),
                new ZooKeeperRequestInfo(ZooKeeperOperation.Sync, path: path),
                cancellationToken
            )
        );

    private static async Task SyncCoreAsync(Task<WireReply> pending) =>
        _ = await pending.ConfigureAwait(false);

    /// <summary>Stops admission and waits for acknowledged close, or throws after local cleanup.</summary>
    /// <exception cref="ZooKeeperCloseException">Local cleanup finished without a server acknowledgment, because the session was not connected or the server did not reply within <c>CloseTimeout</c>. Ephemeral nodes may remain until the server expires the session.</exception>
    /// <exception cref="OperationCanceledException">The caller canceled the wait after local cleanup.</exception>
    public Task CloseAsync(CancellationToken cancellationToken = default) =>
        _session.CloseAsync(cancellationToken);

    /// <summary>Releases owned local resources, without requiring server acknowledgment.</summary>
    /// <remarks>
    /// While the session is connected, this first requests an acknowledged close within <c>CloseTimeout</c>.
    /// An unacknowledged close is not reported; an application that must observe it calls <see cref="CloseAsync"/>.
    /// On a borrowed view, such as the one supplied by the dependency-injection integration, this does nothing.
    /// </remarks>
    public ValueTask DisposeAsync() =>
        _ownsSession ? _session.DisposeAsync() : ValueTask.CompletedTask;

    internal SessionManager Manager => _session;
}
