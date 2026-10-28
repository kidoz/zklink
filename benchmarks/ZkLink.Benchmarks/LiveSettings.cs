using System.Globalization;
using System.Net;

namespace ZkLink.Benchmarks;

internal sealed record LiveSettings(
    int Seconds,
    bool Write,
    int PayloadBytes,
    int Concurrency,
    string Output,
    string Endpoint,
    string Version
)
{
    internal const int DefaultSeconds = 5;
    internal const int MaximumSeconds = 30;
    internal const int MediumConcurrency = 32;
    internal const int MaximumConcurrency = 256;
    internal const int WarmupSeconds = 1;
    internal const int CaseOverheadSeconds = 60;
    internal static readonly TimeSpan OperationGuard = TimeSpan.FromSeconds(5);
    internal static readonly TimeSpan CleanupGuard = TimeSpan.FromSeconds(20);
    internal static readonly TimeSpan SampleInterval = TimeSpan.FromMilliseconds(100);

    internal static LiveSettings Read(
        string seconds,
        string operation,
        string payload,
        string concurrency,
        string output
    )
    {
        static int Number(string value) =>
            int.Parse(value, NumberStyles.None, CultureInfo.InvariantCulture);
        int duration = Number(seconds);
        int bytes = Number(payload);
        int workers = Number(concurrency);
        if (
            duration is < 1 or > MaximumSeconds
            || operation is not ("get" or "set")
            || bytes is not (CodecWorkload.SmallPayloadBytes or CodecWorkload.LargePayloadBytes)
            || workers is not (1 or MediumConcurrency or MaximumConcurrency)
        )
        {
            throw new InvalidOperationException("Invalid live benchmark settings.");
        }
        string Required(string name) =>
            Environment.GetEnvironmentVariable(name)
            ?? throw new InvalidOperationException("Use just bench-live with its owned fixture.");
        string endpoint = Required("ZKLINK_LIVE_BENCHMARK_ENDPOINT");
        string version = Required("ZKLINK_LIVE_BENCHMARK_VERSION");
        if (
            !Guid.TryParseExact(Required("ZKLINK_LIVE_BENCHMARK_RUN_ID"), "N", out _)
            || version is not ("3.8.7" or "3.9.6")
            || !IPEndPoint.TryParse(endpoint, out var address)
            || !IPAddress.IsLoopback(address.Address)
            || !Path.IsPathFullyQualified(output)
            || Path.GetFileName(Path.GetDirectoryName(output))
                ?.StartsWith("zklink-live-benchmarks.", StringComparison.Ordinal) != true
        )
        {
            throw new InvalidOperationException(
                "An owned pinned loopback benchmark fixture is required."
            );
        }
        return new(duration, operation == "set", bytes, workers, output, endpoint, version);
    }
}
