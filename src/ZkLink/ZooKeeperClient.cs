using ZkLink.Configuration;
using ZkLink.Protocol;
using ZkLink.Sessions;
using ZkLink.Transport;

namespace ZkLink;

/// <summary>One explicitly owned ZooKeeper session with bounded asynchronous I/O and no operation replay.</summary>
public sealed partial class ZooKeeperClient : IAsyncDisposable
{
    private readonly SessionManager _session;

    private ZooKeeperClient(SessionManager session) => _session = session;

    /// <summary>Snapshots options before returning its Task, then connects/authenticates within the initial deadline.</summary>
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
        SessionManager? manager = null;
        var transferred = false;
        try
        {
            manager = new SessionManager(options, time, connector, random);
            await manager.Start().WaitAsync(cancellationToken).ConfigureAwait(false);
            var result = new ZooKeeperClient(manager);
            manager = null;
            transferred = true;
            return result;
        }
        catch
        {
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
        }
    }

    /// <summary>Gets current immutable state without session credentials.</summary>
    public ZooKeeperSessionSnapshot Session => _session.Session;

    /// <summary>Atomically subscribes to an initial snapshot and ordered state transitions.</summary>
    public ZooKeeperStateSubscription SubscribeStateChanges() => _session.Subscribe();

    /// <summary>Waits explicitly for readiness or fails on terminal loss; it never creates a replacement session.</summary>
    public Task WaitForConnectionAsync(CancellationToken cancellationToken = default) =>
        _session.WaitForConnectionAsync(cancellationToken);

    /// <summary>Updates future endpoint attempts atomically without replacing identity, chroot or TLS policy.</summary>
    public Task UpdateEndpointsAsync(
        IReadOnlyList<ZooKeeperEndpoint> endpoints,
        CancellationToken cancellationToken = default
    )
    {
        _session.UpdateEndpoints(endpoints, cancellationToken);
        return Task.CompletedTask;
    }

    /// <summary>Synchronizes a path using the current session without implying a globally fresh snapshot.</summary>
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
    public Task CloseAsync(CancellationToken cancellationToken = default) =>
        _session.CloseAsync(cancellationToken);

    /// <summary>Always releases local resources, without requiring server acknowledgment.</summary>
    public ValueTask DisposeAsync() => _session.DisposeAsync();

    internal SessionManager Manager => _session;
}
