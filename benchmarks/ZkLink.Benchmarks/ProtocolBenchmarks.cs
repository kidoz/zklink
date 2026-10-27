using BenchmarkDotNet.Attributes;

namespace ZkLink.Benchmarks;

[MemoryDiagnoser]
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Maintainability",
    "CA1515",
    Justification = "BenchmarkDotNet generates a separate assembly that references this public benchmark type."
)]
public class CodecBenchmarks
{
    private CodecWorkload _workload = null!;

    [ParamsAllValues]
    public CodecCase Workload { get; set; }

    [GlobalSetup]
    public void Setup() => _workload = new CodecWorkload(Workload);

    [Benchmark]
    public int Encode() => _workload.Encode();

    [Benchmark]
    public object Decode() => _workload.Decode();
}

[MemoryDiagnoser]
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Maintainability",
    "CA1515",
    Justification = "BenchmarkDotNet generates a separate assembly that references this public benchmark type."
)]
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Design",
    "CA1001",
    Justification = "BenchmarkDotNet owns this instance and calls GlobalCleanup to dispose the reused memory streams."
)]
public class FramingBenchmarks
{
    private FrameWorkload _workload = null!;

    [Params(CodecWorkload.SmallPayloadBytes, CodecWorkload.LargePayloadBytes)]
    public int PayloadBytes { get; set; }

    [GlobalSetup]
    public async Task SetupAsync()
    {
        _workload = new FrameWorkload(PayloadBytes);
        await _workload.ValidateAsync().ConfigureAwait(true);
    }

    [Benchmark]
    public Task<byte[]?> ReadAsync() => _workload.ReadAsync();

    [Benchmark]
    public Task WriteAsync() => _workload.WriteAsync();

    [GlobalCleanup]
    public void Cleanup() => _workload.Dispose();
}
