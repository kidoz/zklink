using System.Runtime.InteropServices;
using System.Text.Json;
using ZkLink.Protocol;

namespace ZkLink.Protocol.Tests;

public sealed class AllocationBaselineTests(ITestOutputHelper output)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    [Fact]
    [Trait("Acceptance", "T35")]
    public void RecordSmallGetDataCodecAllocationsWithoutTimingGate()
    {
        const int iterations = 10_000;
        WireRequest request = ProtocolFixtures.Reads[0];
        ProtocolLimits limits = ProtocolFixtures.Limits;
        var destination = new byte[RequestCodec.Measure(1, request, limits)];
        byte[] replyBytes = ProtocolFixtures.Bytes("get-data-reply");
        WireReply? last = null;
        for (int i = 0; i < 1000; i++)
        {
            RequestCodec.Encode(destination, 1, request, limits);
            last = ReplyCodec.Decode(replyBytes, 1, request, limits);
        }
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < iterations; i++)
            RequestCodec.Encode(destination, 1, request, limits);
        long encoding = GC.GetAllocatedBytesForCurrentThread() - before;
        before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < iterations; i++)
            last = ReplyCodec.Decode(replyBytes, 1, request, limits);
        long decoding = GC.GetAllocatedBytesForCurrentThread() - before;
        GC.KeepAlive(last);
        double encodedPerOperation = encoding / (double)iterations;
        double decodedPerOperation = decoding / (double)iterations;
        output.WriteLine(
            $"get-data encode: {encodedPerOperation} bytes/op; decode: {decodedPerOperation} bytes/op; iterations: {iterations}"
        );

        string? path = Environment.GetEnvironmentVariable("ZKLINK_ALLOCATION_OUTPUT");
        if (path is not null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            // The baseline is test-tool JSON, not runtime serialization in the driver.
            var report = new Dictionary<string, object>
            {
                ["workload"] = "GetData request encoding and owned response decoding",
                ["iterations"] = iterations,
                ["requestPayloadBytes"] = destination.Length,
                ["responsePayloadBytes"] = replyBytes.Length,
                ["encodedAllocatedBytesPerOperation"] = encodedPerOperation,
                ["decodedAllocatedBytesPerOperation"] = decodedPerOperation,
                ["encodeBudgetBytesPerOperation"] = 1,
                ["decodeBudgetBytesPerOperation"] = 256,
                ["runtime"] = RuntimeInformation.FrameworkDescription,
                ["os"] = RuntimeInformation.OSDescription,
                ["architecture"] = RuntimeInformation.ProcessArchitecture.ToString(),
                ["server"] = "none; pinned Apache-generated fixture",
                ["timingGate"] = false,
            };
            File.WriteAllText(path, JsonSerializer.Serialize(report, JsonOptions));
        }
        // Initial measurement: 128 encoding bytes per 10,000 calls; 176 decoding bytes/call.
        // Coarse initial budgets catch per-call encoding allocations and extra result copies.
        Assert.InRange(encodedPerOperation, 0, 1);
        Assert.InRange(decodedPerOperation, double.Epsilon, 256);
    }
}
