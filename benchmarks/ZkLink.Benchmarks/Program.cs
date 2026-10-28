using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Exporters.Csv;
using BenchmarkDotNet.Exporters.Json;
using BenchmarkDotNet.Running;

namespace ZkLink.Benchmarks;

internal static class Program
{
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Design",
        "CA1031",
        Justification = "The executable boundary must return failure without printing exception text that could contain credentials or paths."
    )]
    internal static async Task<int> Main(string[] args)
    {
        if (args.Length > 0 && args[0] is "--live" or "--watch-fanout")
        {
            try
            {
                if (
                    args
                    is ["--watch-fanout", string watchSeconds, string consumers, string watchOutput]
                )
                {
                    var settings = LiveSettings.Read(
                        watchSeconds,
                        "get",
                        CodecWorkload.SmallPayloadBytes.ToString(
                            System.Globalization.CultureInfo.InvariantCulture
                        ),
                        consumers,
                        watchOutput
                    );
                    return await WatchFanoutBenchmark.RunAsync(settings).ConfigureAwait(false);
                }
                if (
                    args
                    is not [
                        "--live",
                        string seconds,
                        string operation,
                        string payload,
                        string concurrency,
                        string liveOutput,
                    ]
                )
                {
                    throw new InvalidOperationException("Invalid live benchmark arguments.");
                }
                return await LiveBenchmark
                    .RunAsync(
                        LiveSettings.Read(seconds, operation, payload, concurrency, liveOutput)
                    )
                    .ConfigureAwait(false);
            }
            catch (Exception error)
            {
                await Console
                    .Error.WriteLineAsync("Live benchmark failed: " + error.GetType().Name)
                    .ConfigureAwait(false);
                return 1;
            }
        }
        if (args is ["--allocations", string output])
        {
            return await AllocationCheck.RunAsync(output).ConfigureAwait(true);
        }
        var config = ManualConfig
            .Create(DefaultConfig.Instance)
            .AddExporter(JsonExporter.Full, CsvMeasurementsExporter.Default)
            .AddColumn(
                StatisticColumn.OperationsPerSecond,
                StatisticColumn.P50,
                StatisticColumn.P95
            );
        var summaries = BenchmarkSwitcher
            .FromAssembly(typeof(Program).Assembly)
            .Run(args, config)
            .ToArray();
        if (
            args.Contains("--list", StringComparer.Ordinal)
            || args.Contains("--help", StringComparer.Ordinal)
        )
        {
            return 0;
        }
        return
            summaries.Length > 0
            && summaries.All(summary =>
                !summary.HasCriticalValidationErrors
                && summary.Reports.Length > 0
                && summary.Reports.All(report => report.Success)
            )
            ? 0
            : 1;
    }
}
