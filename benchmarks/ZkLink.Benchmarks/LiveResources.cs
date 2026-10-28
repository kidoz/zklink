using System.Diagnostics;

namespace ZkLink.Benchmarks;

// Process-wide observation, including both clients and the benchmark itself.
internal sealed class LiveResources
{
    internal ResourceObservation? Observation { get; private set; }

    internal async Task MeasureAsync(Task gate, Func<Task> workload)
    {
        using var process = Process.GetCurrentProcess();
        using var stop = new CancellationTokenSource();
        await MeasureOwnedAsync(process, stop, gate, workload).ConfigureAwait(false);
    }

    private async Task MeasureOwnedAsync(
        Process process,
        CancellationTokenSource stop,
        Task gate,
        Func<Task> workload
    )
    {
        long workingSet = 0;
        long heap = 0;
        int samples = 0;
        async Task SampleAsync()
        {
            try
            {
                await gate.WaitAsync(stop.Token).ConfigureAwait(false);
                while (!stop.IsCancellationRequested)
                {
                    process.Refresh();
                    workingSet = Math.Max(workingSet, process.WorkingSet64);
                    heap = Math.Max(heap, GC.GetTotalMemory(forceFullCollection: false));
                    samples++;
                    await Task.Delay(LiveSettings.SampleInterval, stop.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        }
        var sampler = SampleAsync();
        long before = GC.GetTotalAllocatedBytes(precise: true);
        var collections = Enumerable
            .Range(0, GC.MaxGeneration + 1)
            .Select(GC.CollectionCount)
            .ToArray();
        try
        {
            await workload().ConfigureAwait(false);
        }
        finally
        {
            await stop.CancelAsync().ConfigureAwait(false);
            await sampler.ConfigureAwait(false);
            long allocated = GC.GetTotalAllocatedBytes(precise: true) - before;
            for (int index = 0; index < collections.Length; index++)
            {
                collections[index] = GC.CollectionCount(index) - collections[index];
            }
            process.Refresh();
            long peak = process.PeakWorkingSet64;
            Observation = new(
                allocated,
                collections,
                workingSet,
                heap,
                peak > 0 ? peak : null,
                samples
            );
        }
    }
}

internal sealed record ResourceObservation(
    long ProcessAllocatedBytes,
    int[] Collections,
    long SampledPeakWorkingSetBytes,
    long SampledPeakManagedHeapBytes,
    long? ProcessLifetimePeakWorkingSetBytes,
    int ResourceSamples
);
