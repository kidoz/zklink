namespace ZkLink;

public sealed partial class ZooKeeperClient
{
    /// <summary>Reads existence and establishes a buffered one-shot watch, including when the node is missing.</summary>
    /// <exception cref="ArgumentException">An argument is invalid, for example a relative path.</exception>
    /// <exception cref="System.IO.InvalidDataException">The encoded request exceeds a configured data, collection or frame limit; nothing was sent.</exception>
    /// <exception cref="ZooKeeperException">The request was not sent, was rejected by the server or has an unknown outcome; <see cref="ZooKeeperException.Outcome"/> says which. Local capacity, deadline and protocol failures use the derived types.</exception>
    /// <exception cref="ZooKeeperOperationCanceledException">The caller canceled the wait. The request outcome is preserved.</exception>
    /// <exception cref="NotSupportedException">The ZooKeeper 3.8 compatibility profile cannot combine this watch mode with an existing registration at the same path.</exception>
    public Task<WatchedResult<ZNodeStat?>> ExistsAndWatchAsync(
        string path,
        CancellationToken cancellationToken = default
    )
    {
        var pending = _session.WatchRead(path, ZooKeeperWatchMode.OneShotExists, cancellationToken);
        return WatchedAsync(ReadExistsAsync(pending.Reply), pending.Watch);
    }

    /// <summary>Reads owned data and Stat while establishing a one-shot data watch in the same request.</summary>
    /// <exception cref="ArgumentException">An argument is invalid, for example a relative path.</exception>
    /// <exception cref="System.IO.InvalidDataException">The encoded request exceeds a configured data, collection or frame limit; nothing was sent.</exception>
    /// <exception cref="ZooKeeperException">The request was not sent, was rejected by the server or has an unknown outcome; <see cref="ZooKeeperException.Outcome"/> says which. Local capacity, deadline and protocol failures use the derived types.</exception>
    /// <exception cref="ZooKeeperOperationCanceledException">The caller canceled the wait. The request outcome is preserved.</exception>
    /// <exception cref="NotSupportedException">The ZooKeeper 3.8 compatibility profile cannot combine this watch mode with an existing registration at the same path.</exception>
    public Task<WatchedResult<ZNodeData>> GetDataAndWatchAsync(
        string path,
        CancellationToken cancellationToken = default
    )
    {
        var pending = _session.WatchRead(path, ZooKeeperWatchMode.OneShotData, cancellationToken);
        return WatchedAsync(ReadDataAsync(pending.Reply), pending.Watch);
    }

    /// <summary>Reads child names and parent Stat while establishing a one-shot child watch in the same request.</summary>
    /// <exception cref="ArgumentException">An argument is invalid, for example a relative path.</exception>
    /// <exception cref="System.IO.InvalidDataException">The encoded request exceeds a configured data, collection or frame limit; nothing was sent.</exception>
    /// <exception cref="ZooKeeperException">The request was not sent, was rejected by the server or has an unknown outcome; <see cref="ZooKeeperException.Outcome"/> says which. Local capacity, deadline and protocol failures use the derived types.</exception>
    /// <exception cref="ZooKeeperOperationCanceledException">The caller canceled the wait. The request outcome is preserved.</exception>
    /// <exception cref="NotSupportedException">The ZooKeeper 3.8 compatibility profile cannot combine this watch mode with an existing registration at the same path.</exception>
    public Task<WatchedResult<ZNodeChildren>> GetChildrenAndWatchAsync(
        string path,
        CancellationToken cancellationToken = default
    )
    {
        var pending = _session.WatchRead(
            path,
            ZooKeeperWatchMode.OneShotChildren,
            cancellationToken
        );
        return WatchedAsync(ReadChildrenAsync(pending.Reply), pending.Watch);
    }

    /// <summary>Registers a buffered persistent watch at a path, optionally including descendants. Reconnect gaps require rereading state.</summary>
    /// <exception cref="ArgumentException">An argument is invalid, for example a relative path.</exception>
    /// <exception cref="System.IO.InvalidDataException">The encoded request exceeds a configured data, collection or frame limit; nothing was sent.</exception>
    /// <exception cref="ZooKeeperException">The request was not sent, was rejected by the server or has an unknown outcome; <see cref="ZooKeeperException.Outcome"/> says which. Local capacity, deadline and protocol failures use the derived types.</exception>
    /// <exception cref="ZooKeeperOperationCanceledException">The caller canceled the wait. The request outcome is preserved.</exception>
    /// <exception cref="NotSupportedException">The ZooKeeper 3.8 compatibility profile cannot combine this watch mode with an existing registration at the same path.</exception>
    public Task<ZooKeeperWatch> AddWatchAsync(
        string path,
        PersistentWatchMode mode,
        CancellationToken cancellationToken = default
    )
    {
        var pending = _session.AddWatch(path, mode, cancellationToken);
        return AddedWatchAsync(pending.Reply, pending.Watch);
    }

    private async Task<ZooKeeperWatch> AddedWatchAsync(
        Task<ZkLink.Protocol.WireReply> task,
        ZooKeeperWatch watch
    )
    {
        try
        {
            await task.ConfigureAwait(false);
            return watch;
        }
        catch
        {
            _session.WatchReadFailed(watch);
            throw;
        }
    }

    private async Task<WatchedResult<T>> WatchedAsync<T>(Task<T> task, ZooKeeperWatch watch)
    {
        try
        {
            return new WatchedResult<T>(await task.ConfigureAwait(false), watch);
        }
        catch
        {
            _session.WatchReadFailed(watch);
            throw;
        }
    }
}
