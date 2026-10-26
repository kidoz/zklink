namespace ZkLink;

public sealed partial class ZooKeeperClient
{
    /// <summary>Reads existence and establishes a buffered one-shot watch, including when the node is missing.</summary>
    public Task<WatchedResult<ZNodeStat?>> ExistsAndWatchAsync(
        string path,
        CancellationToken cancellationToken = default
    )
    {
        var pending = _session.WatchRead(path, ZooKeeperWatchMode.OneShotExists, cancellationToken);
        return WatchedAsync(ReadExistsAsync(pending.Reply), pending.Watch);
    }

    /// <summary>Reads owned data and Stat while establishing a one-shot data watch in the same request.</summary>
    public Task<WatchedResult<ZNodeData>> GetDataAndWatchAsync(
        string path,
        CancellationToken cancellationToken = default
    )
    {
        var pending = _session.WatchRead(path, ZooKeeperWatchMode.OneShotData, cancellationToken);
        return WatchedAsync(ReadDataAsync(pending.Reply), pending.Watch);
    }

    /// <summary>Reads child names and parent Stat while establishing a one-shot child watch in the same request.</summary>
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
