using System.Runtime.InteropServices;
using System.Text.Json;

namespace ZkLink.Benchmarks;

// This tool measures the same reused workloads as BenchmarkDotNet. No stopwatch gate.
internal static class AllocationCheck
{
    private const int WarmupIterations = 2048;
    private const int MeasurementIterations = 4096;
    private const string BudgetFile = "allocation-budgets.json";
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    internal static async Task<int> RunAsync(string output)
    {
        var budgets =
            JsonSerializer.Deserialize<Dictionary<string, double>>(
                await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, BudgetFile))
                    .ConfigureAwait(true)
            ) ?? throw new InvalidDataException("Missing allocation budgets.");
        var rows = new List<AllocationResult>();
        foreach (var name in Enum.GetValues<CodecCase>())
        {
            var workload = new CodecWorkload(name);
            MeasureCodec(workload, name, encode: true, budgets, rows);
            MeasureCodec(workload, name, encode: false, budgets, rows);
        }
        foreach (
            int size in new[] { CodecWorkload.SmallPayloadBytes, CodecWorkload.LargePayloadBytes }
        )
        {
            using var workload = new FrameWorkload(size);
            await workload.ValidateAsync().ConfigureAwait(true);
            await MeasureFrameAsync(workload, size, read: true, budgets, rows).ConfigureAwait(true);
            await MeasureFrameAsync(workload, size, read: false, budgets, rows)
                .ConfigureAwait(true);
        }
        bool complete =
            rows.Count == budgets.Count && rows.All(row => budgets.ContainsKey(row.Case));
        var report = new
        {
            Runtime = RuntimeInformation.FrameworkDescription,
            OS = RuntimeInformation.OSDescription,
            Architecture = RuntimeInformation.ProcessArchitecture.ToString(),
            Server = "none; synthetic Jute records and synchronous MemoryStream framing",
            WarmupIterations,
            MeasurementIterations,
            TimingGate = false,
            Complete = complete,
            Results = rows,
        };
        string fullPath = Path.GetFullPath(output);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        await File.WriteAllTextAsync(fullPath, JsonSerializer.Serialize(report, JsonOptions))
            .ConfigureAwait(true);
        foreach (var row in rows)
        {
            Console.WriteLine(
                FormattableString.Invariant(
                    $"{row.Case}: {row.BytesPerOperation:F2} B/op; budget {row.BudgetBytesPerOperation:F2}; {(row.Passed ? "PASS" : "FAIL")}"
                )
            );
        }
        return complete && rows.All(static row => row.Passed) ? 0 : 1;
    }

    private static void MeasureCodec(
        CodecWorkload workload,
        CodecCase name,
        bool encode,
        Dictionary<string, double> budgets,
        List<AllocationResult> rows
    )
    {
        object? decoded = null;
        int encoded = 0;
        for (int index = 0; index < WarmupIterations; index++)
        {
            if (encode)
            {
                encoded = workload.Encode();
            }
            else
            {
                decoded = workload.Decode();
            }
        }
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int index = 0; index < MeasurementIterations; index++)
        {
            if (encode)
            {
                encoded = workload.Encode();
            }
            else
            {
                decoded = workload.Decode();
            }
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        GC.KeepAlive(decoded);
        if (encode && encoded != workload.RequestBytes)
        {
            throw new InvalidDataException("Allocation check encoded length mismatch.");
        }
        Add(
            name + (encode ? ".Encode" : ".Decode"),
            allocated,
            workload.RequestBytes,
            workload.ReplyBytes,
            budgets,
            rows
        );
    }

    private static async Task MeasureFrameAsync(
        FrameWorkload workload,
        int size,
        bool read,
        Dictionary<string, double> budgets,
        List<AllocationResult> rows
    )
    {
        byte[]? decoded = null;
        for (int index = 0; index < WarmupIterations; index++)
        {
            if (read)
            {
                decoded = await workload.ReadAsync().ConfigureAwait(true);
            }
            else
            {
                await workload.WriteAsync().ConfigureAwait(true);
            }
        }
        int thread = Environment.CurrentManagedThreadId;
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int index = 0; index < MeasurementIterations; index++)
        {
            if (read)
            {
                var operation = workload.ReadAsync();
                if (!operation.IsCompletedSuccessfully)
                {
                    throw new InvalidOperationException(
                        "The framing allocation workload must complete synchronously."
                    );
                }
                decoded = await operation.ConfigureAwait(true);
            }
            else
            {
                var operation = workload.WriteAsync();
                if (!operation.IsCompletedSuccessfully)
                {
                    throw new InvalidOperationException(
                        "The framing allocation workload must complete synchronously."
                    );
                }
                await operation.ConfigureAwait(true);
            }
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        GC.KeepAlive(decoded);
        if (Environment.CurrentManagedThreadId != thread || (read && decoded?.Length != size))
        {
            throw new InvalidOperationException("Invalid framing allocation measurement.");
        }
        Add($"Frame{size}.{(read ? "Read" : "Write")}", allocated, size, size, budgets, rows);
    }

    private static void Add(
        string name,
        long allocated,
        int requestBytes,
        int replyBytes,
        Dictionary<string, double> budgets,
        List<AllocationResult> rows
    )
    {
        if (!budgets.TryGetValue(name, out double budget) || !double.IsFinite(budget) || budget < 0)
        {
            throw new InvalidDataException("Missing or invalid allocation budget for " + name);
        }
        double perOperation = allocated / (double)MeasurementIterations;
        rows.Add(new(name, requestBytes, replyBytes, perOperation, budget, perOperation <= budget));
    }

    private sealed record AllocationResult(
        string Case,
        int RequestBytes,
        int ReplyBytes,
        double BytesPerOperation,
        double BudgetBytesPerOperation,
        bool Passed
    );
}
