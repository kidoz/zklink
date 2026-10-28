using System.Diagnostics;

namespace ZkLink.Benchmarks;

internal sealed class WatchRecoveryRun
{
    private readonly LiveResources _resources = new();
    private readonly LatencyHistogram _gaps = new();
    private long _cutAt;
    private long _pausedAt;
    private long _resumeAt;
    private ZooKeeperSessionState _pausedState;
    private long _releasedAt;
    private long _readyAt;
    private long _lastGapAt;
    internal Task<WatchReceipt?[]>? GapReads { get; private set; }
    internal Task<WatchReceipt?[]>? PendingReads { get; private set; }
    internal Task<WatchReceipt?[]>? ClosingReads { get; private set; }
    internal Task<long>? Readiness { get; private set; }
    internal long Changes { get; private set; }
    internal long ProtectedReads { get; private set; }
    internal bool PausedAdmissionRejected { get; private set; }
    internal bool RestoringAdmissionRejected { get; private set; }

    internal async Task RecoverAsync(
        ZooKeeperClient owner,
        RecoveryProxy proxy,
        IReadOnlyList<WatchConsumer> consumers,
        string root,
        long expectedGeneration,
        WatchCounters counters,
        CancellationToken token
    )
    {
        var reads = consumers.Select(static consumer => consumer.ReadAsync()).ToArray();
        GapReads = Task.WhenAll(reads);
        if (reads.Any(static read => read.IsCompleted))
        {
            throw new InvalidDataException("Unexpected event before the controlled disconnect.");
        }
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task RecoverOwnedAsync()
        {
            _cutAt = Stopwatch.GetTimestamp();
            gate.SetResult();
            proxy.CutAndPause();
            await proxy
                .ReconnectPaused.WaitAsync(RecoverySettings.RecoveryGuard, token)
                .ConfigureAwait(false);
            _pausedAt = Stopwatch.GetTimestamp();
            _pausedState = owner.Session.State;
            if (
                _pausedState
                is not (ZooKeeperSessionState.Disconnected or ZooKeeperSessionState.Connecting)
            )
            {
                throw new InvalidDataException(
                    "Paused reconnect must keep admission closed before authentication."
                );
            }
            Readiness = ReadyAtAsync(owner, token);
            await RequireNotSentAsync(owner, root, token).ConfigureAwait(false);
            PausedAdmissionRejected = true;
            if (Readiness.IsCompleted || reads.Any(static read => read.IsCompleted))
            {
                throw new InvalidDataException(
                    "Readiness/gaps completed while handshake was paused."
                );
            }
            _resumeAt = Stopwatch.GetTimestamp();
            proxy.Resume();
            await proxy
                .RestoreHeld.WaitAsync(RecoverySettings.RecoveryGuard, token)
                .ConfigureAwait(false);
            if (
                owner.Session.State != ZooKeeperSessionState.Restoring
                || Readiness.IsCompleted
                || reads.Any(static read => read.IsCompleted)
            )
            {
                throw new InvalidDataException(
                    "Restore acknowledgment must precede readiness/gaps."
                );
            }
            await RequireNotSentAsync(owner, root, token).ConfigureAwait(false);
            RestoringAdmissionRejected = true;
            _releasedAt = Stopwatch.GetTimestamp();
            proxy.ReleaseRestore();
            _readyAt = await Readiness
                .WaitAsync(RecoverySettings.RecoveryGuard, token)
                .ConfigureAwait(false);
            var receipts = await GapReads
                .WaitAsync(RecoverySettings.RecoveryGuard, token)
                .ConfigureAwait(false);
            for (int index = 0; index < consumers.Count; index++)
            {
                var receipt =
                    receipts[index]
                    ?? throw new InvalidDataException("Persistent watch ended during recovery.");
                Validate(
                    receipt,
                    consumers[index],
                    ZooKeeperWatchEventKind.ReconnectedWithGap,
                    expectedGeneration
                );
                if (receipt.Event.Zxid is not null || receipt.ObservedAt < _releasedAt)
                {
                    throw new InvalidDataException("Invalid reconnect gap metadata/timing.");
                }
                _gaps.Record(receipt.ObservedAt - _releasedAt);
                _lastGapAt = Math.Max(_lastGapAt, receipt.ObservedAt);
            }
            await counters.RequireAsync(consumers.Count, token).ConfigureAwait(false);
        }
        await _resources.MeasureAsync(gate.Task, RecoverOwnedAsync).ConfigureAwait(false);
        if (
            _gaps.Count != consumers.Count
            || _resources.Observation is not { ResourceSamples: > 0 }
        )
        {
            throw new InvalidDataException("Incomplete recovery measurements.");
        }
    }

    internal async Task VerifyAsync(
        ZooKeeperClient owner,
        ZooKeeperClient control,
        IReadOnlyList<WatchConsumer> consumers,
        string root,
        byte[] changed,
        long generation,
        WatchCounters counters,
        CancellationToken token
    )
    {
        for (int offset = 0; offset < consumers.Count; offset += RecoverySettings.BatchSize)
        {
            var batch = consumers.Skip(offset).Take(RecoverySettings.BatchSize).ToArray();
            var reads = batch.Select(static consumer => consumer.ReadAsync()).ToArray();
            PendingReads = Task.WhenAll(reads);
            if (reads.Any(static read => read.IsCompleted))
            {
                throw new InvalidDataException("Unexpected additional reconnect gap/event.");
            }
            var stats = await Task.WhenAll(
                    batch.Select(consumer =>
                        control.SetDataAsync(consumer.Watch.Path, changed, ZNodeVersion.Any, token)
                    )
                )
                .ConfigureAwait(false);
            var receipts = await PendingReads
                .WaitAsync(RecoverySettings.RecoveryGuard, token)
                .ConfigureAwait(false);
            for (int index = 0; index < batch.Length; index++)
            {
                var receipt =
                    receipts[index]
                    ?? throw new InvalidDataException("Restored watch ended before invalidation.");
                Validate(
                    receipt,
                    batch[index],
                    ZooKeeperWatchEventKind.NodeDataChanged,
                    generation
                );
                if (
                    stats[index].Version != 1
                    || (receipt.Event.Zxid is { } zxid && zxid != stats[index].Mzxid)
                )
                {
                    throw new InvalidDataException("Post-recovery mutation metadata mismatch.");
                }
                Changes++;
            }
            await owner.SyncAsync(root, token).ConfigureAwait(false);
            var data = await Task.WhenAll(
                    batch.Select(consumer => owner.GetDataAsync(consumer.Watch.Path, token))
                )
                .ConfigureAwait(false);
            foreach (var value in data)
            {
                if (
                    value.Stat.Version != 1
                    || value.Data is not { } bytes
                    || !bytes.Span.SequenceEqual(changed)
                )
                {
                    throw new InvalidDataException("Protected post-recovery read failed.");
                }
                ProtectedReads++;
            }
        }
        await counters.RequireAsync(consumers.Count, token).ConfigureAwait(false);
    }

    internal async Task<bool> CloseAsync(
        ZooKeeperClient owner,
        IReadOnlyList<WatchConsumer> consumers,
        CancellationToken token
    )
    {
        var reads = consumers.Select(static consumer => consumer.ReadAsync()).ToArray();
        ClosingReads = Task.WhenAll(reads);
        if (reads.Any(static read => read.IsCompleted))
        {
            throw new InvalidDataException("Unexpected buffered post-recovery event.");
        }
        await owner.CloseAsync(token).ConfigureAwait(false);
        var remaining = await ClosingReads
            .WaitAsync(RecoverySettings.RecoveryGuard, token)
            .ConfigureAwait(false);
        return owner.Session.CloseStatus == ZooKeeperCloseStatus.Acknowledged
            && remaining.All(static value => value is null);
    }

    internal object Result(RecoveryWireObservation? wire)
    {
        long held = wire is { HeldAt: > 0 } && _releasedAt > 0 ? _releasedAt - wire.HeldAt : 0;
        return new
        {
            GapCount = _gaps.Count,
            PostRecoveryInvalidations = Changes,
            ProtectedReads,
            PausedAdmissionRejected,
            RestoringAdmissionRejected,
            PausedState = _pausedState.ToString(),
            CutToReconnectPausedObservedMilliseconds = Delta(_cutAt, _pausedAt),
            DeliberatePreHandshakePauseMilliseconds = Delta(_pausedAt, _resumeAt),
            ReconnectHandshakeObservedMilliseconds = Delta(_resumeAt, wire?.SecondHandshakeAt ?? 0),
            ControlledAckHoldMilliseconds = Milliseconds(held),
            RawReadinessMilliseconds = Delta(_resumeAt, _readyAt),
            ActiveReadinessMilliseconds = _readyAt > 0
                ? Milliseconds(Math.Max(0, _readyAt - _resumeAt - held))
                : (double?)null,
            ActiveRestoreAckForwardingMilliseconds = wire
                is { FirstResetAt: > 0, LastResetReplyAt: > 0 }
                ? Milliseconds(Math.Max(0, wire.LastResetReplyAt - wire.FirstResetAt - held))
                : (double?)null,
            AckReleaseToAllGapsMilliseconds = Delta(_releasedAt, _lastGapAt),
            GapTailAfterReadinessMilliseconds = _readyAt > 0
                ? Milliseconds(Math.Max(0, _lastGapAt - _readyAt))
                : (double?)null,
            GapP50 = _gaps.Count > 0 ? _gaps.Percentile(50) : null,
            GapP95 = _gaps.Count > 0 ? _gaps.Percentile(95) : null,
            GapP99 = _gaps.Count > 0 ? _gaps.Percentile(99) : null,
            ResourcesDuringControlledRecovery = _resources.Observation,
        };
    }

    internal IEnumerable<LatencyBucket> Histogram => _gaps.Buckets();

    private static void Validate(
        WatchReceipt receipt,
        WatchConsumer consumer,
        ZooKeeperWatchEventKind kind,
        long generation
    )
    {
        if (
            receipt.Event.Kind != kind
            || receipt.Event.Path != consumer.Watch.Path
            || receipt.Event.RegistrationId != consumer.Watch.RegistrationId
            || receipt.Event.ConnectionGeneration != generation
        )
        {
            throw new InvalidDataException("Restored watch delivery identity mismatch.");
        }
    }

    private static async Task<long> ReadyAtAsync(ZooKeeperClient owner, CancellationToken token)
    {
        await owner.WaitForConnectionAsync(token).ConfigureAwait(false);
        return Stopwatch.GetTimestamp();
    }

    private static async Task RequireNotSentAsync(
        ZooKeeperClient owner,
        string root,
        CancellationToken token
    )
    {
        try
        {
            await owner.SyncAsync(root, token).ConfigureAwait(false);
        }
        catch (ZooKeeperException error) when (error.Outcome == RequestOutcome.NotSent)
        {
            return;
        }
        throw new InvalidDataException("Application admission opened before watch restoration.");
    }

    private static double Milliseconds(long ticks) => ticks * 1000d / Stopwatch.Frequency;

    private static double? Delta(long start, long end) =>
        start > 0 && end >= start ? Milliseconds(end - start) : null;
}
