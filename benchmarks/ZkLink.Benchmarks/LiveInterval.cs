using System.Diagnostics;

namespace ZkLink.Benchmarks;

internal sealed class LiveWorker(string path)
{
    internal string Path { get; } = path;
    internal int Version;
    internal bool Alternate;
    internal long Started;
    internal long Completed;
    internal LatencyHistogram Latency { get; } = new();
}

internal sealed class LiveInterval
{
    private readonly LiveWorker[] _workers;
    private readonly bool _record;
    private int _active;
    private int _peakActive;
    private long _stopAt;
    private long _startedAt;
    private long _finishedAt;
    private readonly LiveResources _resources = new();

    internal LiveInterval(LiveWorker[] workers, bool record)
    {
        _workers = workers;
        _record = record;
    }

    internal async Task RunAsync(
        ZooKeeperClient client,
        LiveSettings settings,
        byte[] initial,
        byte[] alternate,
        int seconds,
        CancellationToken token
    )
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        await RunOwnedAsync(client, settings, initial, alternate, seconds, stop)
            .ConfigureAwait(false);
    }

    private async Task RunOwnedAsync(
        ZooKeeperClient client,
        LiveSettings settings,
        byte[] initial,
        byte[] alternate,
        int seconds,
        CancellationTokenSource stop
    )
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tasks = _workers
            .Select(worker =>
                WorkAsync(worker, client, settings, initial, alternate, gate.Task, stop)
            )
            .ToArray();
        async Task RunWorkersAsync()
        {
            _startedAt = Stopwatch.GetTimestamp();
            _stopAt = _startedAt + (long)seconds * Stopwatch.Frequency;
            gate.SetResult();
            try
            {
                await Task.WhenAll(tasks).ConfigureAwait(false);
            }
            finally
            {
                _finishedAt = Stopwatch.GetTimestamp();
            }
        }
        if (_record)
        {
            await _resources.MeasureAsync(gate.Task, RunWorkersAsync).ConfigureAwait(false);
        }
        else
        {
            await RunWorkersAsync().ConfigureAwait(false);
        }
        if (
            _workers.Any(static worker =>
                worker.Completed == 0 || worker.Completed != worker.Started
            )
            || _active != 0
            || (
                _record
                && (
                    _resources.Observation is not { ResourceSamples: > 0 }
                    || _workers.Any(static worker => worker.Latency.Count != worker.Completed)
                )
            )
        )
        {
            throw new InvalidDataException("Incomplete live benchmark workload.");
        }
    }

    private async Task WorkAsync(
        LiveWorker worker,
        ZooKeeperClient client,
        LiveSettings settings,
        byte[] initial,
        byte[] alternate,
        Task gate,
        CancellationTokenSource stop
    )
    {
        await gate.ConfigureAwait(false);
        try
        {
            while (Stopwatch.GetTimestamp() < _stopAt)
            {
                stop.Token.ThrowIfCancellationRequested();
                worker.Started++;
                int active = Interlocked.Increment(ref _active);
                int observed = Volatile.Read(ref _peakActive);
                while (active > observed)
                {
                    int previous = Interlocked.CompareExchange(ref _peakActive, active, observed);
                    if (previous == observed)
                    {
                        break;
                    }
                    observed = previous;
                }
                long start = Stopwatch.GetTimestamp();
                try
                {
                    if (settings.Write)
                    {
                        bool next = !worker.Alternate;
                        var stat = await client
                            .SetDataAsync(
                                worker.Path,
                                next ? alternate : initial,
                                ZNodeVersion.Any,
                                stop.Token
                            )
                            .ConfigureAwait(false);
                        long finish = Stopwatch.GetTimestamp();
                        if (
                            stat.Version != worker.Version + 1
                            || stat.DataLength != settings.PayloadBytes
                        )
                        {
                            throw new InvalidDataException("Set reply validation failed.");
                        }
                        worker.Version++;
                        worker.Alternate = next;
                        Complete(worker, finish - start);
                    }
                    else
                    {
                        var result = await client
                            .GetDataAsync(worker.Path, stop.Token)
                            .ConfigureAwait(false);
                        long finish = Stopwatch.GetTimestamp();
                        if (
                            result.Data is not { } data
                            || !data.Span.SequenceEqual(initial)
                            || result.Stat.DataLength != settings.PayloadBytes
                        )
                        {
                            throw new InvalidDataException("Get reply validation failed.");
                        }
                        Complete(worker, finish - start);
                    }
                }
                finally
                {
                    Interlocked.Decrement(ref _active);
                }
            }
        }
        catch
        {
            await stop.CancelAsync().ConfigureAwait(false);
            throw;
        }
    }

    private void Complete(LiveWorker worker, long elapsed)
    {
        worker.Completed++;
        if (_record)
        {
            worker.Latency.Record(elapsed);
        }
    }

    internal LiveMeasurement Result()
    {
        var latency = new LatencyHistogram();
        foreach (var worker in _workers)
        {
            latency.Merge(worker.Latency);
        }
        long completed = _workers.Sum(static worker => worker.Completed);
        double elapsed =
            _finishedAt > _startedAt ? (_finishedAt - _startedAt) / (double)Stopwatch.Frequency : 0;
        var resources = _resources.Observation ?? new ResourceObservation(0, [], 0, 0, null, 0);
        return new(
            _workers.Sum(static worker => worker.Started),
            completed,
            _workers.Count(static worker => worker.Completed > 0),
            _peakActive,
            elapsed,
            elapsed > 0 ? completed / elapsed : 0,
            resources.ProcessAllocatedBytes,
            completed > 0 ? (resources.ProcessAllocatedBytes) / (double)completed : 0,
            resources.Collections,
            resources.SampledPeakWorkingSetBytes,
            resources.SampledPeakManagedHeapBytes,
            resources.ProcessLifetimePeakWorkingSetBytes,
            resources.ResourceSamples,
            latency.Count > 0
                ? latency.TotalTicks * 1_000_000d / Stopwatch.Frequency / latency.Count
                : 0,
            latency.MaxTicks * 1_000_000d / Stopwatch.Frequency,
            latency.Count > 0 ? latency.Percentile(50) : null,
            latency.Count > 0 ? latency.Percentile(95) : null,
            latency.Count > 0 ? latency.Percentile(99) : null,
            latency.Buckets().ToArray()
        );
    }
}

internal sealed record LiveMeasurement(
    long Started,
    long Completed,
    int WorkersWithCompletions,
    int PeakOutstandingCalls,
    double ElapsedSecondsIncludingDrain,
    double OperationsPerSecond,
    long ProcessAllocatedBytes,
    double ProcessAllocatedBytesPerOperation,
    int[] Collections,
    long SampledPeakWorkingSetBytes,
    long SampledPeakManagedHeapBytes,
    long? ProcessLifetimePeakWorkingSetBytes,
    int ResourceSamples,
    double MeanLatencyMicroseconds,
    double MaxLatencyMicroseconds,
    LatencyRange? P50,
    LatencyRange? P95,
    LatencyRange? P99,
    LatencyBucket[] Histogram
);
