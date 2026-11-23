using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Threading.Channels;

namespace ZkLink.Testing;

internal sealed record CapturedActivity(
    string Name,
    string? Version,
    TimeSpan Duration,
    ActivityTraceId TraceId,
    ActivitySpanId ParentSpanId,
    ActivityStatusCode Status,
    string? Description,
    string? TraceState,
    KeyValuePair<string, object?>[] Tags,
    KeyValuePair<string, string?>[] Baggage,
    ActivityEvent[] Events
);

internal sealed class TelemetryCapture : IDisposable
{
    private static readonly string[] AllowedTags = ["operation", "outcome", "error", "lifecycle"];

    // Test-side mirror of the driver's closed vocabularies: ZooKeeperOperation,
    // TelemetryOutcome, TelemetryError, TelemetryFault and TelemetryKind. A tag value
    // outside them fails the redaction check. A protocol test compares this mirror
    // with those enums so it cannot drift.
    internal static readonly string[] AllowedValues =
    [
        "Create",
        "Delete",
        "Exists",
        "GetData",
        "SetData",
        "GetAcl",
        "SetAcl",
        "GetChildrenNames",
        "GetChildren",
        "Sync",
        "Check",
        "Multi",
        "CreateWithStat",
        "CheckWatches",
        "RemoveWatches",
        "CreateContainer",
        "CreateTtl",
        "MultiRead",
        "GetEphemerals",
        "GetAllChildrenNumber",
        "AddWatch",
        "WhoAmI",
        "CloseSession",
        "Success",
        "NotSent",
        "Rejected",
        "Unknown",
        "Failed",
        "None",
        "Cancelled",
        "Timeout",
        "Backpressure",
        "Tls",
        "Connection",
        "Session",
        "Protocol",
        "Server",
        "Other",
        "Overflow",
        "Unexpected",
        "Connect",
        "TransportConnect",
        "Authenticate",
        "Restore",
        "Operation",
        "Close",
    ];
    private const int CaptureLimit = 8192;
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(15);
    private readonly ActivityListener _activities;
    private readonly MeterListener _metrics = new();
    private readonly Channel<bool> _changed = Channel.CreateBounded<bool>(CaptureLimit);
    internal ConcurrentQueue<CapturedActivity> Activities { get; } = new();
    internal ConcurrentDictionary<string, double> Values { get; } = new(StringComparer.Ordinal);
    internal ConcurrentQueue<(string Name, double Value)> Histograms { get; } = new();
    internal Action? OnActivity { get; set; }
    internal Action? OnMeasurement { get; set; }
    internal bool Overflowed { get; private set; }

    internal TelemetryCapture()
    {
        _activities = new ActivityListener
        {
            ShouldListenTo = static source => source.Name == "ZkLink",
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) =>
                ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity =>
            {
                OnActivity?.Invoke();
                if (Activities.Count >= CaptureLimit)
                {
                    Overflowed = true;
                    return;
                }
                Activities.Enqueue(
                    new(
                        activity.OperationName,
                        activity.Source.Version,
                        activity.Duration,
                        activity.TraceId,
                        activity.ParentSpanId,
                        activity.Status,
                        activity.StatusDescription,
                        activity.TraceStateString,
                        activity.TagObjects.ToArray(),
                        activity.Baggage.ToArray(),
                        activity.Events.ToArray()
                    )
                );
                _changed.Writer.TryWrite(true);
            },
        };
        ActivitySource.AddActivityListener(_activities);
        _metrics.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == "ZkLink")
                listener.EnableMeasurementEvents(instrument);
        };
        _metrics.SetMeasurementEventCallback<long>(
            (instrument, value, tags, _) => Record(instrument, value, tags)
        );
        _metrics.SetMeasurementEventCallback<double>(
            (instrument, value, tags, _) => Record(instrument, value, tags)
        );
        _metrics.Start();
    }

    private void Record(
        Instrument instrument,
        double value,
        ReadOnlySpan<KeyValuePair<string, object?>> tags
    )
    {
        OnMeasurement?.Invoke();
        string key =
            instrument.Name
            + string.Concat(tags.ToArray().Select(static tag => "|" + tag.Key + "=" + tag.Value));
        if (Values.Count >= CaptureLimit && !Values.ContainsKey(key))
        {
            Overflowed = true;
            return;
        }
        Values[key] = value;
        if (instrument is Histogram<double>)
        {
            if (Histograms.Count >= CaptureLimit)
            {
                Overflowed = true;
                return;
            }
            Histograms.Enqueue((instrument.Name, value));
        }
    }

    internal void Reset()
    {
        Activities.Clear();
        Histograms.Clear();
        Values.Clear();
        while (_changed.Reader.TryRead(out _)) { }
    }

    internal void Collect() => _metrics.RecordObservableInstruments();

    internal double Value(string name) => Values.GetValueOrDefault(name);

    internal async Task WaitForAsync(
        Func<CapturedActivity, bool> predicate,
        CancellationToken token
    )
    {
        while (!Activities.Any(predicate))
            await _changed
                .Reader.ReadAsync(token)
                .AsTask()
                .WaitAsync(Guard, token)
                .ConfigureAwait(false);
    }

    internal void AssertRedacted(params string[] markers)
    {
        foreach (var activity in Activities)
        {
            Assert.True(string.IsNullOrEmpty(activity.Description));
            Assert.True(string.IsNullOrEmpty(activity.TraceState));
            Assert.Empty(activity.Baggage);
            Assert.Empty(activity.Events);
            Assert.All(activity.Tags, static tag => AssertFinite(tag.Key, tag.Value));
            foreach (string marker in markers)
            {
                Assert.DoesNotContain(marker, activity.Name, StringComparison.Ordinal);
                Assert.DoesNotContain(
                    marker,
                    string.Join(',', activity.Tags.Select(static tag => tag.Value)),
                    StringComparison.Ordinal
                );
            }
        }
        foreach (string key in Values.Keys)
        {
            // Keys are "<instrument>|<tag>=<value>..."; each tag must stay finite as well.
            foreach (string part in key.Split('|').Skip(1))
            {
                int separator = part.IndexOf('=', StringComparison.Ordinal);
                Assert.True(separator > 0, key);
                AssertFinite(part[..separator], part[(separator + 1)..]);
            }
            foreach (string marker in markers)
                Assert.DoesNotContain(marker, key, StringComparison.Ordinal);
        }
        Assert.False(Overflowed);
    }

    private static void AssertFinite(string key, object? value)
    {
        Assert.Contains(key, AllowedTags);
        string text = Assert.IsType<string>(value);
        Assert.Contains(text, AllowedValues);
    }

    public void Dispose()
    {
        _activities.Dispose();
        _metrics.Dispose();
    }
}
