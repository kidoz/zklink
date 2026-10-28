using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace ZkLink.Benchmarks;

// Retain only current scalar values; no activities, tags or measurement history.
internal sealed class WatchCounters : IDisposable
{
    private static readonly string[] Names =
    [
        "zklink.watches.active",
        "zklink.events.backlog",
        "zklink.requests.outstanding",
        "zklink.requests.bytes",
        "zklink.requests.tombstones",
        "zklink.sessions.disconnected",
    ];
    private readonly MeterListener _listener = new();
    private readonly long[] _values = new long[Names.Length];
    private int _published;
    private static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(10);

    internal WatchCounters()
    {
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            int index = Array.IndexOf(Names, instrument.Name);
            if (instrument.Meter.Name == "ZkLink" && index >= 0)
            {
                listener.EnableMeasurementEvents(instrument, index);
                Interlocked.Or(ref _published, 1 << index);
            }
        };
        _listener.SetMeasurementEventCallback<long>(
            (_, value, _, state) => Interlocked.Exchange(ref _values[(int)state!], value)
        );
        _listener.Start();
    }

    internal async Task RequireAsync(int activeWatches, CancellationToken token)
    {
        long start = Stopwatch.GetTimestamp();
        while (true)
        {
            _listener.RecordObservableInstruments();
            if (
                Volatile.Read(ref _published) == (1 << Names.Length) - 1
                && Volatile.Read(ref _values[0]) == activeWatches
                && OthersZero()
            )
            {
                return;
            }
            if (Stopwatch.GetElapsedTime(start) >= LiveSettings.OperationGuard)
            {
                throw new InvalidDataException(
                    "Watch/request counters did not reach the expected boundary."
                );
            }
            await Task.Delay(Poll, token).ConfigureAwait(false);
        }
    }

    private bool OthersZero()
    {
        for (int index = 1; index < _values.Length; index++)
        {
            if (Volatile.Read(ref _values[index]) != 0)
            {
                return false;
            }
        }
        return true;
    }

    public void Dispose() => _listener.Dispose();
}
