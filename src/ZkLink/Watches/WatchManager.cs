using System.Text;
using ZkLink.Configuration;
using ZkLink.Protocol;
using ZkLink.Requests;
using ZkLink.Sessions;

namespace ZkLink.Watches;

internal sealed class WatchManager(
    object gate,
    ClientOptionsSnapshot options,
    EventBudget budget,
    SessionManager session
)
{
    private readonly record struct Key(string Path, int Category);

    private sealed class Bucket
    {
        internal readonly HashSet<ZooKeeperWatch> Handles = [];
        internal bool Dirty;
        internal bool Removing;
        internal bool Restoring;
    }

    private enum RestoreCategory
    {
        Data,
        Exists,
        Children,
    }

    private readonly Dictionary<Key, Bucket> _buckets = [];
    private readonly HashSet<Key> _cleanup = [];
    private readonly HashSet<ZooKeeperWatch> _handles = [];
    private long _nextId;
    private bool _finished;
    internal int Count
    {
        get
        {
            lock (gate)
                return _handles.Count;
        }
    }

    internal WatchIntent Begin(string path, ZooKeeperWatchMode mode, ZooKeeperRequestInfo info)
    {
        ZNodePath.Validate(path);
        // An acknowledged watch must also fit a one-path restore frame after chroot expansion.
        _ = NamespaceTranslator.Measure(
            new RestoreRequest(0, [path], [], []),
            options.Paths,
            options.Limits
        );
        var key = new Key(
            path,
            mode == ZooKeeperWatchMode.OneShotChildren ? WatchProtocol.Children : WatchProtocol.Data
        );
        lock (gate)
        {
            if (
                _handles.Count >= options.Settings.MaxWatches
                || (!_buckets.ContainsKey(key) && _buckets.Count >= options.Settings.MaxWatches)
            )
                throw new ZooKeeperBackpressureException(info);
            if (!_buckets.TryGetValue(key, out var bucket))
                _buckets.Add(key, bucket = new Bucket());
            var watch = new ZooKeeperWatch(this, checked(++_nextId), path, mode);
            _handles.Add(watch);
            bucket.Handles.Add(watch);
            return new WatchIntent(this, watch);
        }
    }

    private static Key For(ZooKeeperWatch watch) =>
        new(
            watch.Path,
            watch.Mode == ZooKeeperWatchMode.OneShotChildren
                ? WatchProtocol.Children
                : WatchProtocol.Data
        );

    private static bool Shared(Bucket bucket) =>
        bucket.Handles.Any(static watch => !watch.Suppressed && (watch.Pending || watch.Active));

    internal void Abandon(ZooKeeperWatch watch)
    {
        lock (gate)
        {
            Suppress(watch);
            watch.Pending = false;
            Release(watch);
        }
    }

    private void Replied(ZooKeeperWatch watch, PendingRequest pending, WireReply reply)
    {
        watch.Pending = false;
        bool installed =
            reply.Header.Error == WatchProtocol.Success
            || (
                watch.Mode == ZooKeeperWatchMode.OneShotExists
                && reply.Header.Error == (int)ZooKeeperErrorCode.NoNode
            );
        if (!installed || _finished)
        {
            Abandon(watch);
            return;
        }
        watch.Absent = reply.Header.Error == (int)ZooKeeperErrorCode.NoNode;
        if (watch.Suppressed || pending.Completion.Task.IsCompleted)
        {
            Suppress(watch);
            Cleanup(For(watch));
            Release(watch);
        }
        else
            watch.Active = true;
    }

    private void Suppress(ZooKeeperWatch watch)
    {
        watch.Suppressed = true;
        watch.Active = false;
        while (watch.Queue.Reader.TryRead(out _))
            budget.Release(1);
        watch.Queue.Writer.TryComplete();
    }

    private void Cleanup(Key key, bool restored = false)
    {
        if (_buckets.TryGetValue(key, out var bucket) && !Shared(bucket))
        {
            if (
                !restored
                && !bucket.Restoring
                && session.Session.State != ZooKeeperSessionState.Connected
            )
            {
                Prune(key, bucket);
                return;
            }
            bucket.Dirty = true;
            _cleanup.Add(key);
            session.WakeWriter();
        }
    }

    private void Release(ZooKeeperWatch watch)
    {
        if (!_handles.Remove(watch))
            return;
        var key = For(watch);
        if (_buckets.TryGetValue(key, out var bucket))
        {
            bucket.Handles.Remove(watch);
            if (bucket.Dirty && !Shared(bucket))
            {
                _cleanup.Add(key);
                session.WakeWriter();
            }
            Prune(key, bucket);
        }
    }

    private void Prune(Key key, Bucket bucket)
    {
        if (bucket.Handles.Count == 0 && !bucket.Dirty && !bucket.Removing && !bucket.Restoring)
            _buckets.Remove(key);
    }

    private void RetryCleanup(Key key)
    {
        if (_buckets.TryGetValue(key, out var bucket))
        {
            bucket.Removing = false;
            bucket.Dirty = true;
            _cleanup.Add(key);
            session.WakeWriter();
        }
    }

    internal bool TryRead(ZooKeeperWatch watch, out ZooKeeperWatchEvent? value)
    {
        lock (gate)
        {
            if (!watch.Suppressed && watch.Queue.Reader.TryRead(out value))
            {
                budget.Release(1);
                return true;
            }
            value = null;
            return false;
        }
    }

    internal Task<ZooKeeperWatchRemovalResult> Remove(
        ZooKeeperWatch watch,
        WatchRemovalMode mode,
        CancellationToken token
    )
    {
        if (!Enum.IsDefined(mode))
            throw new ArgumentOutOfRangeException(nameof(mode));
        lock (gate)
        {
            bool active = watch.Active;
            Suppress(watch);
            var key = For(watch);
            bool shared = _buckets.TryGetValue(key, out var bucket) && Shared(bucket);
            if (!watch.Pending)
                Release(watch);
            if (shared)
                return Task.FromResult(
                    new ZooKeeperWatchRemovalResult(
                        watch.RegistrationId,
                        WatchRemovalStatus.SharedRegistrationRetained
                    )
                );
            if (mode == WatchRemovalMode.Local)
            {
                if (active)
                {
                    // Release may have pruned an empty bucket; retain a bounded cleanup intent.
                    if (!_buckets.ContainsKey(key))
                        _buckets.Add(key, bucket ?? new Bucket());
                    Cleanup(key);
                }
                return Task.FromResult(
                    new ZooKeeperWatchRemovalResult(
                        watch.RegistrationId,
                        WatchRemovalStatus.LocalOnly
                    )
                );
            }
            var request = new WatchRequest(
                ZooKeeperOperation.RemoveWatches,
                watch.Path,
                key.Category
            );
            return ConfirmRemovalAsync(
                watch.RegistrationId,
                session.SubmitWatchRemoval(request, token)
            );
        }
    }

    private static async Task<ZooKeeperWatchRemovalResult> ConfirmRemovalAsync(
        long id,
        Task<WireReply> task
    )
    {
        try
        {
            await task.ConfigureAwait(false);
            return new ZooKeeperWatchRemovalResult(id, WatchRemovalStatus.ServerConfirmed);
        }
        catch (ZooKeeperException error) when (error.KnownErrorCode == ZooKeeperErrorCode.NoWatcher)
        {
            return new ZooKeeperWatchRemovalResult(id, WatchRemovalStatus.NoWatcher);
        }
    }

    internal void Dispose(ZooKeeperWatch watch) =>
        _ = Remove(watch, WatchRemovalMode.Local, default);

    internal bool Dispatch(WireNotification notification)
    {
        if (notification.Type == WatchProtocol.SessionEvent)
            return true;
        string path = options.Paths.ToClient(notification.Path!);
        var kind = (ZooKeeperWatchEventKind)notification.Type;
        IEnumerable<ZooKeeperWatch> data = _buckets.TryGetValue(
            new Key(path, WatchProtocol.Data),
            out var dataBucket
        )
            ? dataBucket.Handles
            : [];
        IEnumerable<ZooKeeperWatch> children = _buckets.TryGetValue(
            new Key(path, WatchProtocol.Children),
            out var childBucket
        )
            ? childBucket.Handles
            : [];
        var targets = data.Concat(children)
            .Where(watch =>
                watch.Active
                && !watch.Suppressed
                && watch.Path == path
                && (
                    kind == ZooKeeperWatchEventKind.NodeDeleted
                    || (
                        watch.Mode == ZooKeeperWatchMode.OneShotChildren
                            ? kind
                                is ZooKeeperWatchEventKind.NodeChildrenChanged
                                    or ZooKeeperWatchEventKind.ChildWatchRemoved
                            : kind
                                is ZooKeeperWatchEventKind.NodeCreated
                                    or ZooKeeperWatchEventKind.NodeDataChanged
                                    or ZooKeeperWatchEventKind.DataWatchRemoved
                    )
                )
            )
            .ToArray();
        if (!budget.Reserve(targets.Length))
            return false;
        foreach (var watch in targets)
        {
            watch.Active = false;
            if (
                !watch.Queue.Writer.TryWrite(
                    new ZooKeeperWatchEvent(watch.RegistrationId, kind, path, notification.Zxid)
                )
            )
                throw new InvalidOperationException("Watch queue invariant violated.");
            watch.Queue.Writer.TryComplete();
        }
        return true;
    }

    // Called by the sole writer under the session gate before taking ordinary work.
    internal void PrepareCleanup()
    {
        foreach (var key in _cleanup.ToArray())
        {
            var bucket = _buckets[key];
            if (!bucket.Dirty || bucket.Removing)
                continue;
            if (Shared(bucket))
            {
                // A pending read may fail; retain the old cleanup intent until a live
                // registration consumes or relinquishes this shared server category.
                _cleanup.Remove(key);
                continue;
            }
            if (
                !session.TryWatchCleanup(
                    new WatchRequest(ZooKeeperOperation.RemoveWatches, key.Path, key.Category),
                    new CleanupObserver(this, key),
                    out var task
                )
            )
                return;
            bucket.Dirty = false;
            _cleanup.Remove(key);
            bucket.Removing = true;
            _ = ObserveCleanupAsync(task!);
            return;
        }
    }

    private static async Task ObserveCleanupAsync(Task<WireReply> task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (ZooKeeperException) { }
    }

    internal IReadOnlyList<RestoreRequest> Restoration(long zxid)
    {
        lock (gate)
        {
            var result = new List<RestoreRequest>();
            var data = new List<string?>();
            var exists = new List<string?>();
            var children = new List<string?>();
            int bytes = WatchProtocol.RestoreFrameOverhead;
            int count = 0;
            int target = Math.Min(WatchProtocol.RestoreTargetBytes, options.Limits.MaxFrameBytes);
            void Flush()
            {
                if (count == 0)
                    return;
                result.Add(
                    new RestoreRequest(zxid, data.ToArray(), exists.ToArray(), children.ToArray())
                );
                data.Clear();
                exists.Clear();
                children.Clear();
                bytes = WatchProtocol.RestoreFrameOverhead;
                count = 0;
            }
            foreach (var (key, bucket) in _buckets)
            {
                var active = bucket
                    .Handles.Where(static watch => watch.Active && !watch.Suppressed)
                    .ToArray();
                foreach (
                    RestoreCategory category in active
                        .Select(watch =>
                            key.Category == WatchProtocol.Children ? RestoreCategory.Children
                            : watch.Absent ? RestoreCategory.Exists
                            : RestoreCategory.Data
                        )
                        .Distinct()
                )
                {
                    int cost = checked(
                        sizeof(int)
                        + Encoding.UTF8.GetByteCount(key.Path)
                        + options.Paths.AddedBytes(key.Path)
                    );
                    if (
                        count > 0
                        && (bytes + cost > target || count >= options.Limits.MaxCollectionElements)
                    )
                        Flush();
                    (
                        category == RestoreCategory.Children ? children
                        : category == RestoreCategory.Data ? data
                        : exists
                    ).Add(key.Path);
                    bytes += cost;
                    count++;
                    bucket.Restoring = true;
                }
            }
            Flush();
            return result;
        }
    }

    internal void Restored()
    {
        lock (gate)
        {
            foreach (var pair in _buckets.ToArray())
            {
                if (!pair.Value.Restoring)
                    continue;
                pair.Value.Restoring = false;
                if (!Shared(pair.Value))
                    Cleanup(pair.Key, restored: true);
            }
        }
    }

    internal void Detached()
    {
        _cleanup.Clear();
        foreach (var pair in _buckets.ToArray())
        {
            pair.Value.Dirty = false;
            pair.Value.Removing = false;
            pair.Value.Restoring = false;
            Prune(pair.Key, pair.Value);
        }
    }

    internal void Finish(Exception? error)
    {
        _finished = true;
        foreach (var watch in _handles)
        {
            watch.Active = false;
            watch.Queue.Writer.TryComplete(error);
        }
    }

    internal sealed class WatchIntent(WatchManager manager, ZooKeeperWatch watch) : IRequestObserver
    {
        internal ZooKeeperWatch Watch => watch;

        public void Replied(PendingRequest pending, WireReply reply) =>
            manager.Replied(watch, pending, reply);

        public void Canceled(PendingRequest pending)
        {
            manager.Suppress(watch);
            if (!pending.Sent)
                manager.Abandon(watch);
        }

        public void Abandoned(PendingRequest pending) => manager.Abandon(watch);
    }

    private sealed class CleanupObserver(WatchManager manager, Key key) : IRequestObserver
    {
        public void Replied(PendingRequest pending, WireReply reply) => Done();

        public void Canceled(PendingRequest pending)
        {
            if (!pending.Sent)
                manager.RetryCleanup(key);
        }

        public void Abandoned(PendingRequest pending) => Done();

        private void Done()
        {
            if (manager._buckets.TryGetValue(key, out var bucket))
            {
                bucket.Removing = false;
                manager.Prune(key, bucket);
            }
        }
    }
}
