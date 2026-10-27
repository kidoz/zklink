using ZkLink.Protocol;
using ZkLink.Requests;
using ZkLink.Watches;

namespace ZkLink.Sessions;

internal sealed partial class SessionManager
{
    internal int WatchCount => _watches.Count;

    internal void WakeWriter() => _work.Set();

    internal (ZooKeeperWatch Watch, Task<WireReply> Reply) WatchRead(
        string path,
        ZooKeeperWatchMode mode,
        CancellationToken token
    )
    {
        var operation = mode switch
        {
            ZooKeeperWatchMode.OneShotExists => ZooKeeperOperation.Exists,
            ZooKeeperWatchMode.OneShotData => ZooKeeperOperation.GetData,
            ZooKeeperWatchMode.OneShotChildren => ZooKeeperOperation.GetChildren,
            _ => throw new ArgumentOutOfRangeException(nameof(mode)),
        };
        var info = new ZooKeeperRequestInfo(operation, path: path);
        lock (_gate)
        {
            var intent = _watches.Begin(path, mode, info);
            try
            {
                return (
                    intent.Watch,
                    Scheduler.Submit(
                        new ReadRequest(operation, path, true),
                        info,
                        token,
                        intent,
                        watchOperation: true
                    )
                );
            }
            catch
            {
                _watches.Abandon(intent.Watch);
                throw;
            }
        }
    }

    internal (ZooKeeperWatch Watch, Task<WireReply> Reply) AddWatch(
        string path,
        PersistentWatchMode mode,
        CancellationToken token
    )
    {
        if (!Enum.IsDefined(mode))
            throw new ArgumentOutOfRangeException(nameof(mode));
        var logical =
            mode == PersistentWatchMode.Persistent
                ? ZooKeeperWatchMode.Persistent
                : ZooKeeperWatchMode.PersistentRecursive;
        var info = new ZooKeeperRequestInfo(ZooKeeperOperation.AddWatch, path: path);
        lock (_gate)
        {
            var intent = _watches.Begin(path, logical, info);
            try
            {
                return (
                    intent.Watch,
                    Scheduler.Submit(
                        new WatchRequest(ZooKeeperOperation.AddWatch, path, (int)mode),
                        info,
                        token,
                        intent,
                        watchOperation: true
                    )
                );
            }
            catch
            {
                _watches.Abandon(intent.Watch);
                throw;
            }
        }
    }

    internal void WatchReadFailed(ZooKeeperWatch watch)
    {
        lock (_gate)
        {
            if (watch.Active)
                _watches.Dispose(watch);
            else if (!watch.Suppressed)
                _watches.Abandon(watch);
        }
    }

    internal Task<WireReply> SubmitWatchRemoval(
        WatchRequest request,
        IRequestObserver observer,
        CancellationToken token
    ) =>
        Scheduler.Submit(
            request,
            new ZooKeeperRequestInfo(ZooKeeperOperation.RemoveWatches, path: request.Path),
            token,
            observer,
            watchOperation: true
        );

    internal bool TryWatchCleanup(
        WatchRequest request,
        IRequestObserver observer,
        out Task<WireReply>? task
    )
    {
        if (
            Session.State != ZooKeeperSessionState.Connected
            || _closing
            || !Scheduler.MaintenanceAvailable
        )
        {
            task = null;
            return false;
        }
        task = Scheduler.Submit(
            request,
            new ZooKeeperRequestInfo(ZooKeeperOperation.RemoveWatches, path: request.Path),
            default,
            observer,
            watchOperation: true,
            maintenance: true
        );
        return true;
    }

    internal void PrepareWatchCleanup(long generation)
    {
        lock (_gate)
        {
            if (_active?.Generation == generation && Scheduler.IsAttached(generation) && !_closing)
                _watches.PrepareCleanup();
        }
    }

    private void DetachRequests()
    {
        Scheduler.Detach();
        _watches.Detached();
    }
}
