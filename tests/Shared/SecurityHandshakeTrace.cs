using System.Diagnostics;
using System.Diagnostics.Tracing;

namespace ZkLink.Testing;

internal enum SecurityProbeUse
{
    Setup,
    Rejection,
    FreshControl,
}

internal enum SecurityHandshakeEvent
{
    Start,
    Stop,
    Failed,
}

internal sealed record SecurityHandshakeObservation(
    int Actor,
    SecurityProbeUse Use,
    SecurityHandshakeEvent Event,
    DateTimeOffset ObservedAtUtc,
    double ElapsedMilliseconds
)
{
    internal string ToJson() =>
        FormattableString.Invariant(
            $"{{\"kind\":\"tls_handshake\",\"actor\":{Actor},\"use\":\"{Use}\",\"event\":\"{Event}\",\"observed_at_utc\":\"{ObservedAtUtc:O}\",\"elapsed_ms\":{ElapsedMilliseconds:F4}}}"
        );
}

internal sealed record SecurityHandshakeSnapshot(
    IReadOnlyList<SecurityHandshakeObservation> Events,
    int Dropped,
    int Unattributed,
    int OutputFailures
);

// Observe only event names and our own bounded context. EventSource payloads can
// contain a target hostname or exception text and must never enter this trace.
internal sealed class SecurityHandshakeTrace : EventListener
{
    internal const int MaximumEvents = 2048;
    internal const int MaximumActors = 32;
    private const string SourceName = "System.Net.Security";
    private static readonly AsyncLocal<ProbeContext?> Current = new();
    private readonly object _gate = new();
    private readonly List<SecurityHandshakeObservation> _events = [];
    private readonly long _started = Stopwatch.GetTimestamp();
    private readonly int _capacity;
    private readonly Action<string>? _output;
    private bool _ready;
    private int _dropped;
    private int _unattributed;
    private int _outputFailures;

    internal SecurityHandshakeTrace(Action<string>? output = null, int capacity = MaximumEvents)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(capacity, MaximumEvents);
        _capacity = capacity;
        _output = output;
        // EventListener's base constructor invokes OnEventSourceCreated before
        // these fields exist; no scoped connection can start until we return.
        Volatile.Write(ref _ready, true);
    }

    internal IDisposable EnterProbe(int actor, SecurityProbeUse use)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(actor);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(actor, MaximumActors);
        var previous = Current.Value;
        Current.Value = new(this, actor, use);
        return new ProbeScope(previous);
    }

    internal SecurityHandshakeSnapshot Snapshot()
    {
        lock (_gate)
        {
            return new(
                _events.ToArray(),
                _dropped,
                Volatile.Read(ref _unattributed),
                Volatile.Read(ref _outputFailures)
            );
        }
    }

    protected override void OnEventSourceCreated(EventSource eventSource)
    {
        if (eventSource.Name == SourceName)
        {
            EnableEvents(eventSource, EventLevel.Informational);
        }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Design",
        "CA1031",
        Justification = "Output failures are counted and fail the enclosing experiment; a diagnostic sink must not change the TLS result."
    )]
    protected override void OnEventWritten(EventWrittenEventArgs eventData)
    {
        if (!Volatile.Read(ref _ready) || eventData.EventSource.Name != SourceName)
        {
            return;
        }
        SecurityHandshakeEvent? kind = eventData.EventName switch
        {
            "HandshakeStart" => SecurityHandshakeEvent.Start,
            "HandshakeStop" => SecurityHandshakeEvent.Stop,
            "HandshakeFailed" => SecurityHandshakeEvent.Failed,
            _ => null,
        };
        if (kind is null)
        {
            return;
        }
        var context = Current.Value;
        if (context?.Owner != this)
        {
            Interlocked.Increment(ref _unattributed);
            return;
        }
        var observation = new SecurityHandshakeObservation(
            context.Actor,
            context.Use,
            kind.Value,
            DateTimeOffset.UtcNow,
            Stopwatch.GetElapsedTime(_started).TotalMilliseconds
        );
        lock (_gate)
        {
            if (_events.Count == _capacity)
            {
                _dropped++;
                return;
            }
            _events.Add(observation);
        }
        try
        {
            _output?.Invoke(observation.ToJson());
        }
        catch
        {
            // EventSource must not turn a listener/output failure into a TLS
            // failure. The enclosing experiment rejects this numeric counter.
            Interlocked.Increment(ref _outputFailures);
        }
    }

    private sealed record ProbeContext(
        SecurityHandshakeTrace Owner,
        int Actor,
        SecurityProbeUse Use
    );

    private sealed class ProbeScope(ProbeContext? previous) : IDisposable
    {
        public void Dispose() => Current.Value = previous;
    }
}
