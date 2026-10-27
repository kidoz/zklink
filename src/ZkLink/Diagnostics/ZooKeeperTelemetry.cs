using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Threading.Channels;

namespace ZkLink.Diagnostics;

internal enum TelemetryKind
{
    Connect,
    TransportConnect,
    Authenticate,
    Restore,
    Operation,
    Close,
}

internal enum TelemetryOutcome
{
    Success,
    NotSent,
    Rejected,
    Unknown,
    Failed,
}

internal enum TelemetryError
{
    None,
    Cancelled,
    Timeout,
    Backpressure,
    Tls,
    Connection,
    Session,
    Protocol,
    Other,
}

internal enum TelemetrySample
{
    Span,
    Heartbeat,
    Disconnected,
    Negotiated,
    Barrier,
}

internal readonly record struct TelemetrySpan(
    TimeProvider Time,
    long Started,
    DateTimeOffset UtcStarted,
    TelemetryKind Kind,
    ZooKeeperOperation? Operation,
    ActivityContext Parent
)
{
    internal void Complete(Exception? error = null) => ZooKeeperTelemetry.Complete(this, error);
}

internal static class ZooKeeperTelemetry
{
    internal const string Name = "ZkLink";
    internal const int Capacity = 1024;
    private static readonly string? Version = typeof(ZooKeeperClient)
        .Assembly.GetName()
        .Version?.ToString();
    private static ActivitySource? Source;
    private static Meter? Meter;
    private static volatile bool _initialized;
    private static readonly ZooKeeperOperation[] Operations = Enum.GetValues<ZooKeeperOperation>();
    private static readonly TelemetryOutcome[] Outcomes =
    [
        TelemetryOutcome.Success,
        TelemetryOutcome.NotSent,
        TelemetryOutcome.Rejected,
        TelemetryOutcome.Unknown,
    ];
    private static readonly long[][] Counts = Operations
        .Select(static _ => new long[Outcomes.Length])
        .ToArray();
    private static Histogram<double>? Duration,
        Lifecycle,
        Heartbeat,
        Disconnected,
        Negotiated;
    private static readonly Channel<Record> Queue = Channel.CreateBounded<Record>(
        new BoundedChannelOptions(Capacity)
        {
            SingleReader = true,
            AllowSynchronousContinuations = false,
            FullMode = BoundedChannelFullMode.Wait,
        }
    );
    internal static long Requests,
        Bytes,
        Tombstones,
        Watches,
        Backlog,
        DisconnectedSessions;
    private static long _unknown,
        _reconnects,
        _overflows,
        _dropped,
        _listenerErrors;

    private readonly record struct Stamp(
        DateTimeOffset UtcStarted,
        TelemetryKind Kind,
        ZooKeeperOperation? Operation,
        ActivityContext Parent
    );

    private readonly record struct Record(
        TelemetrySample Sample,
        Stamp Span = default,
        double Seconds = 0,
        TelemetryOutcome Outcome = default,
        TelemetryError Error = default,
        TaskCompletionSource? Barrier = null
    );

    static ZooKeeperTelemetry()
    {
        if (ExecutionContext.IsFlowSuppressed())
            _ = Task.Run(DispatchAsync);
        else
            using (ExecutionContext.SuppressFlow())
                _ = Task.Run(DispatchAsync);
    }

    private static void InitializeInstruments()
    {
        Publish(() => Source = new ActivitySource(Name, Version));
        Meter? meter = null;
        Publish(() => meter = new Meter(Name, Version));
        if (meter is null)
        {
            _initialized = true;
            return;
        }
        Meter = meter;
        Publish(() => Duration = meter.CreateHistogram<double>("zklink.operation.duration", "s"));
        Publish(() => Lifecycle = meter.CreateHistogram<double>("zklink.lifecycle.duration", "s"));
        Publish(() => Heartbeat = meter.CreateHistogram<double>("zklink.heartbeat.roundtrip", "s"));
        Publish(() =>
            Disconnected = meter.CreateHistogram<double>(
                "zklink.session.disconnected.duration",
                "s"
            )
        );
        Publish(() =>
            Negotiated = meter.CreateHistogram<double>("zklink.session.negotiated_timeout", "s")
        );
        Publish(() =>
            meter.CreateObservableCounter("zklink.operation.count", ObserveCounts, "{operation}")
        );
        Publish(() =>
            meter.CreateObservableCounter(
                "zklink.operation.unknown",
                () => Interlocked.Read(ref _unknown),
                "{operation}"
            )
        );
        Publish(() =>
            meter.CreateObservableCounter(
                "zklink.reconnect.attempts",
                () => Interlocked.Read(ref _reconnects),
                "{attempt}"
            )
        );
        Publish(() =>
            meter.CreateObservableCounter(
                "zklink.event.overflows",
                () => Interlocked.Read(ref _overflows),
                "{overflow}"
            )
        );
        Publish(() =>
            meter.CreateObservableCounter(
                "zklink.telemetry.dropped",
                () => Interlocked.Read(ref _dropped),
                "{record}"
            )
        );
        Publish(() =>
            meter.CreateObservableCounter(
                "zklink.telemetry.listener_errors",
                () => Interlocked.Read(ref _listenerErrors),
                "{error}"
            )
        );
        Publish(() =>
            meter.CreateObservableUpDownCounter(
                "zklink.requests.outstanding",
                () => Interlocked.Read(ref Requests),
                "{request}"
            )
        );
        Publish(() =>
            meter.CreateObservableUpDownCounter(
                "zklink.requests.bytes",
                () => Interlocked.Read(ref Bytes),
                "By"
            )
        );
        Publish(() =>
            meter.CreateObservableUpDownCounter(
                "zklink.requests.tombstones",
                () => Interlocked.Read(ref Tombstones),
                "{request}"
            )
        );
        Publish(() =>
            meter.CreateObservableUpDownCounter(
                "zklink.watches.active",
                () => Interlocked.Read(ref Watches),
                "{watch}"
            )
        );
        Publish(() =>
            meter.CreateObservableUpDownCounter(
                "zklink.events.backlog",
                () => Interlocked.Read(ref Backlog),
                "{event}"
            )
        );
        Publish(() =>
            meter.CreateObservableUpDownCounter(
                "zklink.sessions.disconnected",
                () => Interlocked.Read(ref DisconnectedSessions),
                "{session}"
            )
        );
        _initialized = true;
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Design",
        "CA1031",
        Justification = "Listener registration/publication callbacks are application code. Failure must not poison driver type initialization; the unavailable instrument is omitted and the failure counted."
    )]
    private static void Publish(Action action)
    {
        try
        {
            action();
        }
        catch (Exception)
        {
            Interlocked.Increment(ref _listenerErrors);
        }
        finally
        {
            Activity.Current = null;
        }
    }

    internal static void Initialize() { }

    internal static TelemetrySpan Begin(
        TimeProvider time,
        TelemetryKind kind,
        ZooKeeperOperation? operation = null
    )
    {
        var current = Activity.Current?.Context ?? default;
        var parent = new ActivityContext(current.TraceId, current.SpanId, current.TraceFlags);
        return new(
            time,
            time.GetTimestamp(),
            (!_initialized || Source?.HasListeners() == true) ? time.GetUtcNow() : default,
            kind,
            operation,
            parent
        );
    }

    internal static async Task<T> ObserveAsync<T>(Task<T> task, TelemetrySpan span)
    {
        try
        {
            var value = await task.ConfigureAwait(false);
            span.Complete();
            return value;
        }
        catch (Exception error)
        {
            span.Complete(error);
            throw;
        }
    }

    internal static async Task ObserveAsync(Task task, TelemetrySpan span)
    {
        try
        {
            await task.ConfigureAwait(false);
            span.Complete();
        }
        catch (Exception error)
        {
            span.Complete(error);
            throw;
        }
    }

    internal static void Complete(TelemetrySpan span, Exception? error)
    {
        var outcome =
            error is IZooKeeperRequestFailure failure
                ? failure.Outcome switch
                {
                    RequestOutcome.Rejected => TelemetryOutcome.Rejected,
                    RequestOutcome.Unknown => TelemetryOutcome.Unknown,
                    _ => TelemetryOutcome.NotSent,
                }
            : error is null ? TelemetryOutcome.Success
            : TelemetryOutcome.NotSent;
        if (
            span.Kind != TelemetryKind.Operation
            && error is not null
            && error is not IZooKeeperRequestFailure
        )
            outcome = TelemetryOutcome.Failed;
        var category = error switch
        {
            null => TelemetryError.None,
            OperationCanceledException => TelemetryError.Cancelled,
            ZooKeeperTimeoutException or TimeoutException => TelemetryError.Timeout,
            ZooKeeperBackpressureException => TelemetryError.Backpressure,
            ZooKeeperConnectionException { Failure: ZooKeeperConnectionFailure.Tls } =>
                TelemetryError.Tls,
            ZooKeeperConnectionException { Failure: ZooKeeperConnectionFailure.Protocol } =>
                TelemetryError.Protocol,
            InvalidDataException => TelemetryError.Protocol,
            ZooKeeperConnectionException or IOException => TelemetryError.Connection,
            ZooKeeperSessionException => TelemetryError.Session,
            _ => TelemetryError.Other,
        };
        if (span.Kind == TelemetryKind.Operation && span.Operation is { } operation)
        {
            int index = Array.IndexOf(Operations, operation);
            if (index >= 0)
                Interlocked.Increment(ref Counts[index][(int)outcome]);
            if (outcome == TelemetryOutcome.Unknown)
                Interlocked.Increment(ref _unknown);
        }
        if (
            !_initialized
            || Source?.HasListeners() == true
            || (
                span.Kind == TelemetryKind.Operation
                    ? Duration?.Enabled == true
                    : Lifecycle?.Enabled == true
            )
        )
            Post(
                new(
                    TelemetrySample.Span,
                    new Stamp(span.UtcStarted, span.Kind, span.Operation, span.Parent),
                    span.Time.GetElapsedTime(span.Started).TotalSeconds,
                    outcome,
                    category
                )
            );
    }

    internal static void Reconnect() => Interlocked.Increment(ref _reconnects);

    internal static void Overflow() => Interlocked.Increment(ref _overflows);

    internal static void RecordHeartbeat(TimeSpan elapsed)
    {
        if (!_initialized || Heartbeat?.Enabled == true)
            Post(new(TelemetrySample.Heartbeat, Seconds: elapsed.TotalSeconds));
    }

    internal static void RecordDisconnected(TimeSpan elapsed)
    {
        if (!_initialized || Disconnected?.Enabled == true)
            Post(new(TelemetrySample.Disconnected, Seconds: elapsed.TotalSeconds));
    }

    internal static void RecordNegotiated(TimeSpan timeout)
    {
        if (!_initialized || Negotiated?.Enabled == true)
            Post(new(TelemetrySample.Negotiated, Seconds: timeout.TotalSeconds));
    }

    private static void Post(Record value)
    {
        if (!Queue.Writer.TryWrite(value))
            Interlocked.Increment(ref _dropped);
    }

    private static IEnumerable<Measurement<long>> ObserveCounts()
    {
        for (int i = 0; i < Operations.Length; i++)
            foreach (var outcome in Outcomes)
                yield return new(
                    Interlocked.Read(ref Counts[i][(int)outcome]),
                    new("operation", Operations[i].ToString()),
                    new("outcome", outcome.ToString())
                );
    }

    // A test-only barrier; normal driver completion never waits for listener work.
    internal static async Task FlushAsync(CancellationToken token)
    {
        var completion = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        await Queue
            .Writer.WriteAsync(new(TelemetrySample.Barrier, Barrier: completion), token)
            .ConfigureAwait(false);
        await completion.Task.WaitAsync(token).ConfigureAwait(false);
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Design",
        "CA1031",
        Justification = "Application telemetry callbacks must not terminate the diagnostic dispatcher or driver; failures are counted without retaining exception text."
    )]
    private static async Task DispatchAsync()
    {
        InitializeInstruments();
        await foreach (var value in Queue.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            try
            {
                switch (value.Sample)
                {
                    case TelemetrySample.Barrier:
                        value.Barrier!.SetResult();
                        break;
                    case TelemetrySample.Heartbeat:
                        Heartbeat?.Record(value.Seconds);
                        break;
                    case TelemetrySample.Disconnected:
                        Disconnected?.Record(value.Seconds);
                        break;
                    case TelemetrySample.Negotiated:
                        Negotiated?.Record(value.Seconds);
                        break;
                    case TelemetrySample.Span:
                        DeliverSpan(value);
                        break;
                }
            }
            catch (Exception)
            {
                Interlocked.Increment(ref _listenerErrors);
            }
            finally
            {
                Activity.Current = null;
            }
        }
    }

    private static void DeliverSpan(Record record)
    {
        var span = record.Span;
        var tags = new TagList
        {
            { "outcome", record.Outcome.ToString() },
            { "error", record.Error.ToString() },
        };
        if (span.Operation is { } operation)
            tags.Add("operation", operation.ToString());
        if (span.Kind == TelemetryKind.Operation)
            Duration?.Record(record.Seconds, tags);
        else
        {
            tags.Add("lifecycle", span.Kind.ToString());
            Lifecycle?.Record(record.Seconds, tags);
        }
        if (span.UtcStarted == default)
            return;
        using var activity = Source?.StartActivity(
            span.Kind switch
            {
                TelemetryKind.Connect => "zklink.connect",
                TelemetryKind.TransportConnect => "zklink.transport.connect",
                TelemetryKind.Authenticate => "zklink.authenticate",
                TelemetryKind.Restore => "zklink.restore",
                TelemetryKind.Close => "zklink.close",
                _ => "zklink.operation",
            },
            ActivityKind.Client,
            span.Parent,
            tags,
            startTime: span.UtcStarted
        );
        if (activity is not null)
        {
            activity.SetStatus(
                record.Error == TelemetryError.None
                    ? ActivityStatusCode.Ok
                    : ActivityStatusCode.Error
            );
            activity.SetEndTime(
                (span.UtcStarted + TimeSpan.FromSeconds(record.Seconds)).UtcDateTime
            );
        }
    }
}
