using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Exporters.Csv;
using BenchmarkDotNet.Exporters.Json;
using BenchmarkDotNet.Running;

namespace ZkLink.Benchmarks;

internal static class Program
{
    internal static async Task<int> Main(string[] args)
    {
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
