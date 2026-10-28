using System.Diagnostics;

namespace ZkLink.Benchmarks;

internal sealed class WatchMutationState
{
    internal int Version;
    internal bool Alternate;
}

internal sealed class WatchFanoutInterval(bool record)
{
    private readonly LiveResources _resources = new();
    private long _startedAt;
    private long _finishedAt;
    internal long MutationsStarted { get; private set; }
    internal long MutationsCompleted { get; private set; }
    internal long ProgressRequests { get; private set; }
    internal Task<WatchReceipt?[]>? PendingReads { get; private set; }

    internal async Task RunAsync(
        ZooKeeperClient owner,
        ZooKeeperClient mutator,
        string path,
        IReadOnlyList<WatchConsumer> consumers,
        WatchMutationState state,
        WatchCounters counters,
        byte[] initial,
        byte[] alternate,
        int seconds,
        CancellationToken token
    )
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task RunMutationsAsync()
        {
            _startedAt = Stopwatch.GetTimestamp();
            long stopAt = _startedAt + (long)seconds * Stopwatch.Frequency;
            gate.SetResult();
            try
            {
                while (Stopwatch.GetTimestamp() < stopAt)
                {
                    var reads = consumers.Select(static consumer => consumer.ReadAsync()).ToArray();
                    if (reads.Any(static read => read.IsCompleted))
                    {
                        throw new InvalidDataException("Unexpected event before mutation.");
                    }
                    bool next = !state.Alternate;
                    byte[] payload = next ? alternate : initial;
                    MutationsStarted++;
                    long start = Stopwatch.GetTimestamp();
                    var stat = await mutator
                        .SetDataAsync(path, payload, ZNodeVersion.Any, token)
                        .ConfigureAwait(false);
                    if (stat.Version != state.Version + 1 || stat.DataLength != payload.Length)
                    {
                        throw new InvalidDataException("Watch mutation acknowledgment mismatch.");
                    }
                    state.Version++;
                    state.Alternate = next;
                    PendingReads = Task.WhenAll(reads);
                    var receipts = await PendingReads
                        .WaitAsync(LiveSettings.OperationGuard, token)
                        .ConfigureAwait(false);
                    for (int index = 0; index < consumers.Count; index++)
                    {
                        var receipt =
                            receipts[index]
                            ?? throw new InvalidDataException("Watch ended during mutation.");
                        var watch = consumers[index].Watch;
                        var value = receipt.Event;
                        if (
                            receipt.ObservedAt < start
                            || value.Kind != ZooKeeperWatchEventKind.NodeDataChanged
                            || value.Path != path
                            || value.RegistrationId != watch.RegistrationId
                            || value.ConnectionGeneration != owner.Session.ConnectionGeneration
                            || (value.Zxid is { } zxid && zxid != stat.Mzxid)
                        )
                        {
                            throw new InvalidDataException("Watch delivery validation failed.");
                        }
                        if (record)
                        {
                            consumers[index].Deliveries++;
                            consumers[index].Latency.Record(receipt.ObservedAt - start);
                        }
                    }
                    await owner.SyncAsync(path, token).ConfigureAwait(false);
                    ProgressRequests++;
                    var data = await owner.GetDataAsync(path, token).ConfigureAwait(false);
                    ProgressRequests++;
                    if (
                        data.Stat.Version != state.Version
                        || data.Data is not { } bytes
                        || !bytes.Span.SequenceEqual(payload)
                    )
                    {
                        throw new InvalidDataException("Watch progress read mismatch.");
                    }
                    await counters.RequireAsync(consumers.Count, token).ConfigureAwait(false);
                    MutationsCompleted++;
                    if (
                        record
                        && consumers.Any(consumer =>
                            consumer.Deliveries != MutationsCompleted
                            || consumer.Latency.Count != MutationsCompleted
                        )
                    )
                    {
                        throw new InvalidDataException("Watch delivery count mismatch.");
                    }
                }
            }
            finally
            {
                _finishedAt = Stopwatch.GetTimestamp();
            }
        }
        if (record)
        {
            await _resources.MeasureAsync(gate.Task, RunMutationsAsync).ConfigureAwait(false);
        }
        else
        {
            await RunMutationsAsync().ConfigureAwait(false);
        }
        if (
            MutationsCompleted == 0
            || MutationsStarted != MutationsCompleted
            || ProgressRequests != MutationsCompleted * 2
            || (record && _resources.Observation is not { ResourceSamples: > 0 })
        )
        {
            throw new InvalidDataException("Incomplete watch fan-out measurement.");
        }
    }

    internal WatchFanoutMeasurement Result(IReadOnlyList<WatchConsumer> consumers)
    {
        var histogram = new LatencyHistogram();
        foreach (var consumer in consumers)
        {
            histogram.Merge(consumer.Latency);
        }
        double elapsed =
            _finishedAt > _startedAt ? (_finishedAt - _startedAt) / (double)Stopwatch.Frequency : 0;
        var resources = _resources.Observation;
        return new(
            MutationsStarted,
            MutationsCompleted,
            histogram.Count,
            ProgressRequests,
            elapsed,
            elapsed > 0 ? MutationsCompleted / elapsed : 0,
            elapsed > 0 ? histogram.Count / elapsed : 0,
            consumers.Select(static consumer => consumer.Deliveries).ToArray(),
            histogram.Count > 0
                ? histogram.TotalTicks * 1_000_000d / Stopwatch.Frequency / histogram.Count
                : 0,
            histogram.MaxTicks * 1_000_000d / Stopwatch.Frequency,
            histogram.Count > 0 ? histogram.Percentile(50) : null,
            histogram.Count > 0 ? histogram.Percentile(95) : null,
            histogram.Count > 0 ? histogram.Percentile(99) : null,
            resources,
            MutationsCompleted > 0
                ? resources?.ProcessAllocatedBytes / (double)MutationsCompleted
                : null,
            histogram.Count > 0 ? resources?.ProcessAllocatedBytes / (double)histogram.Count : null,
            histogram.Buckets().ToArray()
        );
    }
}

internal sealed record WatchFanoutMeasurement(
    long MutationsStarted,
    long MutationsCompleted,
    long DeliveriesCompleted,
    long ProgressRequests,
    double ElapsedSecondsIncludingDrain,
    double MutationsPerSecond,
    double DeliveriesPerSecond,
    long[] PerConsumerDeliveries,
    double MeanLatencyMicroseconds,
    double MaxLatencyMicroseconds,
    LatencyRange? P50,
    LatencyRange? P95,
    LatencyRange? P99,
    ResourceObservation? Resources,
    double? ProcessAllocatedBytesPerMutation,
    double? ProcessAllocatedBytesPerDelivery,
    LatencyBucket[] Histogram
);
