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
    IWatchSession session
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
        Persistent,
        Recursive,
    }

    private readonly object _gate = gate;
    private readonly Dictionary<Key, Bucket> _buckets = [];
    private readonly HashSet<Key> _cleanup = [];
    private readonly Dictionary<string, Bucket> _recursive = new(StringComparer.Ordinal);
    private readonly HashSet<ZooKeeperWatch> _handles = [];
    private long _nextId;
    private bool _finished;
    internal int Count
    {
        get
        {
            lock (_gate)
                return _handles.Count;
        }
    }

    internal WatchIntent Begin(string path, ZooKeeperWatchMode mode, ZooKeeperRequestInfo info)
    {
        ZNodePath.Validate(path);
        // An acknowledged watch must also fit a one-path restore frame after chroot expansion.
        bool persistent =
            mode is ZooKeeperWatchMode.Persistent or ZooKeeperWatchMode.PersistentRecursive;
        _ = NamespaceTranslator.Measure(
            persistent
                ? new RestoreRequest(0, [], [], [], [path], [])
                : new RestoreRequest(0, [path], [], []),
            options.Paths,
            options.Limits
        );
        var key = new Key(path, Category(mode));
        lock (_gate)
        {
            ValidateCompatible(key, info);
            if (
                _handles.Count >= options.Settings.MaxWatches
                || (!_buckets.ContainsKey(key) && _buckets.Count >= options.Settings.MaxWatches)
            )
                throw new ZooKeeperBackpressureException(info);
            if (!_buckets.TryGetValue(key, out var bucket))
            {
                _buckets.Add(key, bucket = new Bucket());
                if (key.Category == WatchProtocol.PersistentRecursive)
                    _recursive.Add(key.Path, bucket);
            }
            var watch = new ZooKeeperWatch(
                this,
                checked(++_nextId),
                path,
                mode,
                persistent ? budget.Limit : 1
            );
            _handles.Add(watch);
            bucket.Handles.Add(watch);
            return new WatchIntent(this, watch);
        }
    }

    private void ValidateCompatible(Key key, ZooKeeperRequestInfo info)
    {
        SessionGate.RequireHeld(_gate);
        if (options.Settings.CompatibilityProfile == ZooKeeperCompatibilityProfile.ZooKeeper39)
            return;
        foreach (
            int category in new[]
            {
                WatchProtocol.Data,
                WatchProtocol.Children,
                WatchProtocol.Persistent,
                WatchProtocol.PersistentRecursive,
            }
        )
        {
            if (
                category == key.Category
                || (category <= WatchProtocol.Data && key.Category <= WatchProtocol.Data)
            )
                continue;
            if (!_buckets.TryGetValue(new Key(key.Path, category), out var bucket))
                continue;
            if (Shared(bucket))
                throw new NotSupportedException(
                    "The ZooKeeper 3.8 profile cannot mix one-shot, persistent and recursive modes at the same path."
                );
            if (
                bucket.Dirty
                || bucket.Removing
                || bucket.Restoring
                || bucket.Handles.Any(static watch => watch.Pending)
            )
                throw new ZooKeeperBackpressureException(info);
        }
    }

    private int RemovalCategory(Key key) =>
        options.Settings.CompatibilityProfile == ZooKeeperCompatibilityProfile.ZooKeeper38
        && key.Category is WatchProtocol.Persistent or WatchProtocol.PersistentRecursive
            ? WatchProtocol.Any
            : key.Category;

    private static int Category(ZooKeeperWatchMode mode) =>
        mode switch
        {
            ZooKeeperWatchMode.OneShotChildren => WatchProtocol.Children,
            ZooKeeperWatchMode.Persistent => WatchProtocol.Persistent,
            ZooKeeperWatchMode.PersistentRecursive => WatchProtocol.PersistentRecursive,
            _ => WatchProtocol.Data,
        };

    private static Key For(ZooKeeperWatch watch) => new(watch.Path, Category(watch.Mode));

    private static bool Shared(Bucket bucket) =>
        bucket.Handles.Any(static watch => !watch.Suppressed && (watch.Pending || watch.Active));

    internal void Abandon(ZooKeeperWatch watch)
    {
        lock (_gate)
        {
            Suppress(watch);
            watch.Pending = false;
            Release(watch);
        }
    }

    private void Replied(ZooKeeperWatch watch, PendingRequest pending, WireReply reply)
    {
        SessionGate.RequireHeld(_gate);
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
            SetActive(watch, true);
    }

    private void SetActive(ZooKeeperWatch watch, bool active)
    {
        SessionGate.RequireHeld(_gate);
        if (watch.Active == active)
            return;
        watch.Active = active;
        session.Telemetry.WatchDelta(active ? 1 : -1);
    }

    private void Suppress(ZooKeeperWatch watch)
    {
        SessionGate.RequireHeld(_gate);
        watch.Suppressed = true;
        SetActive(watch, false);
        while (watch.Queue.Reader.TryRead(out _))
            budget.Release(1);
        watch.Queue.Writer.TryComplete();
    }

    private void Cleanup(Key key, bool restored = false)
    {
        SessionGate.RequireHeld(_gate);
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
        SessionGate.RequireHeld(_gate);
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
        SessionGate.RequireHeld(_gate);
        if (bucket.Handles.Count == 0 && !bucket.Dirty && !bucket.Removing && !bucket.Restoring)
        {
            _buckets.Remove(key);
            if (key.Category == WatchProtocol.PersistentRecursive)
                _recursive.Remove(key.Path);
        }
    }

    private void RetryCleanup(Key key)
    {
        SessionGate.RequireHeld(_gate);
        if (_buckets.TryGetValue(key, out var bucket))
        {
            bucket.Removing = false;
            if (session.Session.State != ZooKeeperSessionState.Connected && !bucket.Restoring)
            {
                bucket.Dirty = false;
                _cleanup.Remove(key);
                Prune(key, bucket);
                return;
            }
            bucket.Dirty = true;
            _cleanup.Add(key);
            session.WakeWriter();
        }
    }

    private void RejectCleanup(Key key)
    {
        SessionGate.RequireHeld(_gate);
        if (_buckets.TryGetValue(key, out var bucket))
        {
            bucket.Removing = false;
            bucket.Dirty = true;
            // Retain the bound after a definitive rejection, without a tight retry loop.
            _cleanup.Remove(key);
        }
    }

    internal bool TryRead(ZooKeeperWatch watch, out ZooKeeperWatchEvent? value)
    {
        lock (_gate)
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
        lock (_gate)
        {
            var key = For(watch);
            if (
                mode == WatchRemovalMode.ServerConfirmed
                && _buckets.TryGetValue(key, out var removing)
                && removing.Removing
            )
            {
                throw new ZooKeeperBackpressureException(
                    new ZooKeeperRequestInfo(ZooKeeperOperation.RemoveWatches, path: watch.Path)
                );
            }
            bool active = watch.Active;
            Suppress(watch);
            bool shared = _buckets.TryGetValue(key, out var bucket) && Shared(bucket);
            if (mode == WatchRemovalMode.ServerConfirmed && !shared)
            {
                if (bucket is null)
                {
                    if (_buckets.Count >= options.Settings.MaxWatches)
                        throw new ZooKeeperBackpressureException(
                            new ZooKeeperRequestInfo(
                                ZooKeeperOperation.RemoveWatches,
                                path: watch.Path
                            )
                        );
                    _buckets.Add(key, bucket = new Bucket());
                    if (key.Category == WatchProtocol.PersistentRecursive)
                        _recursive.Add(key.Path, bucket);
                }
                bucket.Removing = true;
                // This ordered request covers older cleanup intents. A newer intent
                // can set Dirty again while its response is outstanding.
                bucket.Dirty = false;
                _cleanup.Remove(key);
            }
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
                    {
                        if (_buckets.Count >= options.Settings.MaxWatches)
                            throw new ZooKeeperBackpressureException(
                                new ZooKeeperRequestInfo(
                                    ZooKeeperOperation.RemoveWatches,
                                    path: watch.Path
                                )
                            );
                        bucket ??= new Bucket();
                        _buckets.Add(key, bucket);
                        if (key.Category == WatchProtocol.PersistentRecursive)
                            _recursive.Add(key.Path, bucket);
                    }
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
                RemovalCategory(key)
            );
            var observer = new CleanupObserver(this, key);
            try
            {
                return ConfirmRemovalAsync(
                    watch.RegistrationId,
                    session.SubmitWatchRemoval(request, observer, token),
                    observer
                );
            }
            catch
            {
                observer.UnobservedFailure();
                throw;
            }
        }
    }

    private static async Task<ZooKeeperWatchRemovalResult> ConfirmRemovalAsync(
        long id,
        Task<WireReply> task,
        CleanupObserver observer
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
        finally
        {
            observer.UnobservedFailure();
        }
    }

    internal void Dispose(ZooKeeperWatch watch) =>
        _ = Remove(watch, WatchRemovalMode.Local, default);

    internal bool Dispatch(WireNotification notification)
    {
        SessionGate.RequireHeld(_gate);
        if (notification.Type == WatchProtocol.SessionEvent)
            return true;
        string path = options.Paths.ToClient(notification.Path!);
        var kind = (ZooKeeperWatchEventKind)notification.Type;
        var targets = new List<ZooKeeperWatch>();
        AddTargets(path, WatchProtocol.Data);
        AddTargets(path, WatchProtocol.Children);
        AddTargets(path, WatchProtocol.Persistent);
        if (kind == ZooKeeperWatchEventKind.PersistentWatchRemoved)
            AddTargets(path, WatchProtocol.PersistentRecursive);
        else if (
            kind
            is ZooKeeperWatchEventKind.NodeCreated
                or ZooKeeperWatchEventKind.NodeDeleted
                or ZooKeeperWatchEventKind.NodeDataChanged
        )
        {
            // Walk component boundaries, so /a never observes /ab. Root is included once.
            var lookup = _recursive.GetAlternateLookup<ReadOnlySpan<char>>();
            ReadOnlySpan<char> ancestor = path;
            while (true)
            {
                if (lookup.TryGetValue(ancestor, out var bucket))
                    AddBucket(bucket);
                if (ancestor.Length == 1)
                    break;
                int separator = ancestor.LastIndexOf('/');
                ancestor = ancestor[..(separator == 0 ? 1 : separator)];
            }
        }

        if (!budget.Reserve(targets.Count))
            return false;
        foreach (var watch in targets)
        {
            bool complete =
                !watch.Persistent || kind == ZooKeeperWatchEventKind.PersistentWatchRemoved;
            if (complete)
                SetActive(watch, false);
            Publish(watch, kind, path, notification.Zxid);
            if (complete)
                watch.Queue.Writer.TryComplete();
        }
        return true;

        void AddTargets(string watchedPath, int category)
        {
            if (!_buckets.TryGetValue(new Key(watchedPath, category), out var bucket))
                return;
            AddBucket(bucket);
        }

        void AddBucket(Bucket bucket)
        {
            foreach (var watch in bucket.Handles)
            {
                if (!watch.Active || watch.Suppressed)
                    continue;
                bool matches = watch.Persistent
                    ? kind
                        is ZooKeeperWatchEventKind.NodeCreated
                            or ZooKeeperWatchEventKind.NodeDeleted
                            or ZooKeeperWatchEventKind.NodeDataChanged
                            or ZooKeeperWatchEventKind.NodeChildrenChanged
                            or ZooKeeperWatchEventKind.PersistentWatchRemoved
                    : kind == ZooKeeperWatchEventKind.NodeDeleted
                        || (
                            watch.Mode == ZooKeeperWatchMode.OneShotChildren
                                ? kind
                                    is ZooKeeperWatchEventKind.NodeChildrenChanged
                                        or ZooKeeperWatchEventKind.ChildWatchRemoved
                                : kind
                                    is ZooKeeperWatchEventKind.NodeCreated
                                        or ZooKeeperWatchEventKind.NodeDataChanged
                                        or ZooKeeperWatchEventKind.DataWatchRemoved
                        );
                if (matches)
                    targets.Add(watch);
            }
        }
    }

    private void Publish(
        ZooKeeperWatch watch,
        ZooKeeperWatchEventKind kind,
        string path,
        long? zxid
    )
    {
        SessionGate.RequireHeld(_gate);
        if (
            !watch.Queue.Writer.TryWrite(
                new ZooKeeperWatchEvent(
                    watch.RegistrationId,
                    kind,
                    path,
                    zxid,
                    session.Session.ConnectionGeneration
                )
            )
        )
            throw new InvalidOperationException("Watch queue invariant violated.");
    }

    // Called after all reset acknowledgments, under the session gate before scheduler attachment.
    internal bool Reconnected()
    {
        SessionGate.RequireHeld(_gate);
        var targets = _handles
            .Where(static watch => watch.Persistent && watch.Active && !watch.Suppressed)
            .ToArray();
        if (!budget.Reserve(targets.Length))
            return false;
        foreach (var watch in targets)
            Publish(watch, ZooKeeperWatchEventKind.ReconnectedWithGap, watch.Path, null);
        return true;
    }

    // Called by the sole writer under the session gate before taking ordinary work.
    internal void PrepareCleanup()
    {
        SessionGate.RequireHeld(_gate);
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
            // Submit may synchronously notify the observer. Clear the older intent
            // first so a cancellation can install a fresh retry without being erased.
            bucket.Dirty = false;
            _cleanup.Remove(key);
            if (
                !session.TryWatchCleanup(
                    new WatchRequest(
                        ZooKeeperOperation.RemoveWatches,
                        key.Path,
                        RemovalCategory(key)
                    ),
                    new CleanupObserver(this, key),
                    out var task
                )
            )
            {
                bucket.Dirty = true;
                _cleanup.Add(key);
                return;
            }
            bucket.Removing = !task!.IsCompleted;
            _ = ObserveCleanupAsync(task);
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
        catch (OperationCanceledException) { }
    }

    internal IReadOnlyList<RestoreRequest> Restoration(long zxid)
    {
        lock (_gate)
        {
            var result = new List<RestoreRequest>();
            Build(extended: false);
            Build(extended: true);
            return result;

            void Build(bool extended)
            {
                var paths = new List<string?>[Enum.GetValues<RestoreCategory>().Length];
                for (int index = 0; index < paths.Length; index++)
                    paths[index] = [];
                int overhead = extended
                    ? WatchProtocol.ExtendedRestoreFrameOverhead
                    : WatchProtocol.RestoreFrameOverhead;
                int bytes = overhead;
                int count = 0;
                int target = Math.Min(
                    WatchProtocol.RestoreTargetBytes,
                    options.Limits.MaxFrameBytes
                );
                void Flush()
                {
                    if (count == 0)
                        return;
                    result.Add(
                        new RestoreRequest(
                            zxid,
                            paths[(int)RestoreCategory.Data].ToArray(),
                            paths[(int)RestoreCategory.Exists].ToArray(),
                            paths[(int)RestoreCategory.Children].ToArray(),
                            extended ? paths[(int)RestoreCategory.Persistent].ToArray() : null,
                            extended ? paths[(int)RestoreCategory.Recursive].ToArray() : null
                        )
                    );
                    foreach (var list in paths)
                        list.Clear();
                    bytes = overhead;
                    count = 0;
                }
                foreach (var (key, bucket) in _buckets)
                {
                    bool persistent =
                        key.Category
                        is WatchProtocol.Persistent
                            or WatchProtocol.PersistentRecursive;
                    if (persistent != extended)
                        continue;
                    var categories = bucket
                        .Handles.Where(static watch => watch.Active && !watch.Suppressed)
                        .Select(watch =>
                            key.Category switch
                            {
                                WatchProtocol.Children => RestoreCategory.Children,
                                WatchProtocol.Persistent => RestoreCategory.Persistent,
                                WatchProtocol.PersistentRecursive => RestoreCategory.Recursive,
                                _ => watch.Absent ? RestoreCategory.Exists : RestoreCategory.Data,
                            }
                        )
                        .Distinct();
                    foreach (var category in categories)
                    {
                        int cost = checked(
                            sizeof(int)
                            + Encoding.UTF8.GetByteCount(key.Path)
                            + options.Paths.AddedBytes(key.Path)
                        );
                        if (
                            count > 0
                            && (
                                bytes + cost > target
                                || count >= options.Limits.MaxCollectionElements
                            )
                        )
                            Flush();
                        paths[(int)category].Add(key.Path);
                        bytes += cost;
                        count++;
                        bucket.Restoring = true;
                    }
                }
                Flush();
            }
        }
    }

    internal void Restored()
    {
        lock (_gate)
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
        SessionGate.RequireHeld(_gate);
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
        SessionGate.RequireHeld(_gate);
        _finished = true;
        foreach (var watch in _handles)
        {
            SetActive(watch, false);
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
        private bool _observed;

        public void Replied(PendingRequest pending, WireReply reply)
        {
            _observed = true;
            if (reply.Header.Error is WatchProtocol.Success or (int)ZooKeeperErrorCode.NoWatcher)
                Done();
            else
                manager.RejectCleanup(key);
        }

        public void Canceled(PendingRequest pending)
        {
            _observed = true;
            if (!pending.Sent)
                manager.RetryCleanup(key);
        }

        public void Abandoned(PendingRequest pending)
        {
            _observed = true;
            Done();
        }

        internal void UnobservedFailure()
        {
            lock (manager._gate)
            {
                if (!_observed)
                {
                    _observed = true;
                    manager.RetryCleanup(key);
                }
            }
        }

        private void Done()
        {
            SessionGate.RequireHeld(manager._gate);
            if (manager._buckets.TryGetValue(key, out var bucket))
            {
                bucket.Removing = false;
                manager.Prune(key, bucket);
            }
        }
    }
}
