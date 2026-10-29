using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;

namespace ZkLink.FaultTests;

internal sealed record SoakSettings(int Seconds, int Seed, string ResultFile)
{
    internal const int MinimumSeconds = 30;
    internal const int MaximumSeconds = 86_400;
    private const int TestOverheadSeconds = 180;
    private const int MillisecondsPerSecond = 1000;
    internal const int MaximumTestMilliseconds =
        (MaximumSeconds + TestOverheadSeconds) * MillisecondsPerSecond;

    internal static SoakSettings Read()
    {
        string Required(string name) =>
            Environment.GetEnvironmentVariable(name)
            ?? throw new InvalidOperationException("Run just zk-soak with its owned fixture.");
        if (
            Required("ZKLINK_FAULT_ROLE") != "soak"
            || !int.TryParse(
                Required("ZKLINK_SOAK_DURATION_SECONDS"),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out int seconds
            )
            || seconds is < MinimumSeconds or > MaximumSeconds
            || !int.TryParse(
                Required("ZKLINK_SOAK_SEED"),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out int seed
            )
            || seed < 0
        )
        {
            throw new InvalidOperationException("Invalid bounded soak duration/seed.");
        }
        string file = Required("ZKLINK_SOAK_RESULT_FILE");
        if (
            !Path.IsPathFullyQualified(file)
            || Path.GetFileName(Path.GetDirectoryName(file))
                ?.StartsWith("zklink-soak-faults.", StringComparison.Ordinal) != true
        )
        {
            throw new InvalidOperationException("An owned soak evidence directory is required.");
        }
        return new(seconds, seed, file);
    }
}

// Fixed-size observation only; activity/sample histories would themselves distort a soak.
internal sealed class SoakMetrics : IDisposable
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
    private readonly long[] _values = new long[Names.Length];
    private readonly MeterListener _listener = new();
    private int _published;
    internal static readonly TimeSpan Guard = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(25);

    internal SoakMetrics()
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

    internal async Task QuiescentAsync(CancellationToken token)
    {
        await UntilAsync(
                () =>
                {
                    _listener.RecordObservableInstruments();
                    return Volatile.Read(ref _published) == (1 << Names.Length) - 1
                        && _values.All(static value => value == 0);
                },
                "Owned counters did not return to zero.",
                token
            )
            .ConfigureAwait(true);
    }

    internal static async Task UntilAsync(
        Func<bool> condition,
        string failure,
        CancellationToken token
    )
    {
        var timer = Stopwatch.StartNew();
        while (!condition())
        {
            if (timer.Elapsed >= Guard)
            {
                throw new TimeoutException(failure);
            }
            await Task.Delay(Poll, token).ConfigureAwait(true);
        }
    }

    internal static Task PortsClosedAsync(
        int[] listenerPorts,
        int[] upstreamPorts,
        int targetPort,
        CancellationToken token
    )
    {
        Assert.All(
            listenerPorts.Concat(upstreamPorts),
            static port => Assert.InRange(port, IPEndPoint.MinPort + 1, IPEndPoint.MaxPort)
        );
        return UntilAsync(
            () =>
            {
                var network = IPGlobalProperties.GetIPGlobalProperties();
                bool connections = network
                    .GetActiveTcpConnections()
                    .Any(connection =>
                        IPAddress.IsLoopback(connection.LocalEndPoint.Address)
                        && IPAddress.IsLoopback(connection.RemoteEndPoint.Address)
                        && (
                            listenerPorts.Contains(connection.LocalEndPoint.Port)
                            || listenerPorts.Contains(connection.RemoteEndPoint.Port)
                            || (
                                upstreamPorts.Contains(connection.LocalEndPoint.Port)
                                && connection.RemoteEndPoint.Port == targetPort
                            )
                            || (
                                upstreamPorts.Contains(connection.RemoteEndPoint.Port)
                                && connection.LocalEndPoint.Port == targetPort
                            )
                        )
                        && connection.State
                            is TcpState.Established
                                or TcpState.CloseWait
                                or TcpState.SynSent
                                or TcpState.SynReceived
                    );
                bool listeners = network
                    .GetActiveTcpListeners()
                    .Any(endpoint =>
                        IPAddress.IsLoopback(endpoint.Address)
                        && listenerPorts.Contains(endpoint.Port)
                    );
                return !connections && !listeners;
            },
            "Owned proxy connections/listeners remain after disposal.",
            token
        );
    }

    public void Dispose() => _listener.Dispose();
}

internal sealed class SoakTotals
{
    internal int Rounds;
    internal long CrudStarted;
    internal long CrudSucceeded;
    internal long NotSent;
    internal long Unknown;
    internal long Reconnects;
    internal long WatchEvents;
}

internal sealed class SoakEvidence : IAsyncDisposable
{
    internal const int WarmupRounds = 5;
    private const int JournalBufferBytes = 4096;
    private const int MaximumPeriodicSamples = 1440;
    private const int MinimumSampleSeconds = 10;
    private const long RetainedHeapAllowance = 16L * 1024 * 1024;
    private const int ThreadAllowance = 32;
    private readonly SoakSettings _settings;
    private readonly StreamWriter _writer;
    private long _baselineHeap;
    private int _baselineThreads;
    private int _samples;
    private Exception? _reportedFailure;
    internal TimeSpan SampleInterval { get; }

    internal SoakEvidence(SoakSettings settings)
    {
        _settings = settings;
        _writer = new StreamWriter(
            new FileStream(
                settings.ResultFile,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.Read,
                bufferSize: JournalBufferBytes,
                FileOptions.Asynchronous
            )
        );
        SampleInterval = TimeSpan.FromSeconds(
            Math.Max(
                MinimumSampleSeconds,
                (settings.Seconds + MaximumPeriodicSamples - 1) / MaximumPeriodicSamples
            )
        );
    }

    internal async Task SampleAsync(
        string kind,
        SoakTotals totals,
        TimeSpan elapsed,
        CancellationToken token
    )
    {
        long heap = GC.GetTotalMemory(forceFullCollection: true);
        using var process = Process.GetCurrentProcess();
        int threads = process.Threads.Count;
        long workingSet = process.WorkingSet64;
        if (kind == "baseline")
        {
            _baselineHeap = heap;
            _baselineThreads = threads;
        }
        Assert.InRange(++_samples, 1, MaximumPeriodicSamples + 4);
        string line = FormattableString.Invariant(
            $"{{\"kind\":\"{kind}\",\"reported_at_utc\":\"{DateTimeOffset.UtcNow:O}\",\"seed\":{_settings.Seed},\"duration_seconds\":{_settings.Seconds},\"elapsed_ms\":{elapsed.TotalMilliseconds:F0},\"rounds\":{totals.Rounds},\"crud_started\":{totals.CrudStarted},\"crud_succeeded\":{totals.CrudSucceeded},\"canceled_not_sent\":{totals.NotSent},\"canceled_unknown\":{totals.Unknown},\"reconnects\":{totals.Reconnects},\"watch_events\":{totals.WatchEvents},\"retained_heap_bytes\":{heap},\"working_set_bytes\":{workingSet},\"threads\":{threads},\"heap_growth_limit_bytes\":{RetainedHeapAllowance},\"thread_growth_limit\":{ThreadAllowance},\"owned_counters\":0,\"owned_live_tcp\":0,\"owned_listeners\":0}}"
        );
        await _writer.WriteLineAsync(line.AsMemory(), token).ConfigureAwait(true);
        await _writer.FlushAsync(token).ConfigureAwait(true);
        TestContext.Current.TestOutputHelper?.WriteLine(line);
        if (_baselineHeap != 0)
        {
            Assert.True(
                heap <= _baselineHeap + RetainedHeapAllowance,
                "Retained managed heap exceeded the declared growth allowance."
            );
            Assert.True(
                threads <= _baselineThreads + ThreadAllowance,
                "Native thread count exceeded the declared growth allowance."
            );
        }
    }

    internal async Task FailureAsync(Exception failure, SoakTotals totals, TimeSpan elapsed)
    {
        if (ReferenceEquals(failure, _reportedFailure))
        {
            return;
        }
        // The type and counters are safe; exception messages can contain user data.
        var request = failure as IZooKeeperRequestFailure;
        string errorCode = request?.ErrorCode?.ToString(CultureInfo.InvariantCulture) ?? "null";
        string outcome = request is null ? "null" : $"\"{request.Outcome}\"";
        string operation = request is null ? "null" : $"\"{request.Request.Operation}\"";
        string line = FormattableString.Invariant(
            $"{{\"kind\":\"failure\",\"reported_at_utc\":\"{DateTimeOffset.UtcNow:O}\",\"elapsed_ms\":{elapsed.TotalMilliseconds:F0},\"seed\":{_settings.Seed},\"round\":{totals.Rounds},\"error_type\":\"{failure.GetType().Name}\",\"error_code\":{errorCode},\"request_operation\":{operation},\"request_outcome\":{outcome},\"crud_started\":{totals.CrudStarted},\"crud_succeeded\":{totals.CrudSucceeded}}}"
        );
        await _writer.WriteLineAsync(line).ConfigureAwait(true);
        await _writer.FlushAsync().ConfigureAwait(true);
        _reportedFailure = failure;
    }

    public ValueTask DisposeAsync() => _writer.DisposeAsync();
}
