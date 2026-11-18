using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text.Json;
using ZkLink.Protocol;

namespace ZkLink.Protocol.Tests;

public sealed class AllocationBaselineTests(ITestOutputHelper output)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Acceptance", "T35")]
    public async Task DecodedDataBecomesAPublicResultWithinTheAllocationBudget(bool multi)
    {
        const int payloadBytes = 64 * 1024;
        const int iterations = 256;
        var encodedBuffer = new byte[sizeof(int) + payloadBytes];
        BinaryPrimitives.WriteInt32BigEndian(encodedBuffer, payloadBytes);
        encodedBuffer[^1] = 42;
        var reader = new JuteReader(encodedBuffer, ProtocolFixtures.Limits);
        var data = new DataResult(
            reader.ReadBuffer(),
            ProtocolFixtures.Stat with
            {
                DataLength = payloadBytes,
            }
        );
        Array.Clear(encodedBuffer);
        WireResult result = multi
            ? new MultiResult([new WireMultiMember((int)ZooKeeperOperation.GetData, 0, data)])
            : data;
        var reply = Task.FromResult(new WireReply(new ReplyHeader(1, 1, 0), result));

        // A completed reply keeps the actual result mapping on the measuring thread.
        // Warm it once, then count only the allocations made after decoding.
        ZNodeData node = await MapAsync().ConfigureAwait(true);
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < iterations; i++)
        {
            node = await MapAsync().ConfigureAwait(true);
        }
        var bytesPerResult = (GC.GetAllocatedBytesForCurrentThread() - before) / (double)iterations;
        output.WriteLine(
            $"{(multi ? "MultiRead" : "GetData")} 64 KiB result mapping: {bytesPerResult} bytes/op"
        );
        Assert.Equal(42, node.Data!.Value.Span[^1]);
        Assert.Equal(payloadBytes, node.Stat.DataLength);
        Assert.InRange(bytesPerResult, 0, 1024);

        async ValueTask<ZNodeData> MapAsync()
        {
            if (!multi)
            {
                return await ZooKeeperClient.ReadDataAsync(reply).ConfigureAwait(true);
            }
            var members = await ZooKeeperClient.ReadReadMultiAsync(reply).ConfigureAwait(true);
            return ((ZooKeeperDataReadResult)members[0]).Value;
        }
    }

    [Fact]
    [Trait("Acceptance", "T35")]
    public void RecordSmallGetDataCodecAllocationsWithoutTimingGate()
    {
        const int iterations = 10_000;
        var request = ProtocolFixtures.Reads[0];
        var limits = ProtocolFixtures.Limits;
        var destination = new byte[RequestCodec.Measure(1, request, limits)];
        var replyBytes = ProtocolFixtures.Bytes("get-data-reply");
        WireReply? last = null;
        for (var i = 0; i < 1000; i++)
        {
            RequestCodec.Encode(destination, 1, request, limits);
            last = ReplyCodec.DecodeForValidatedRequest(replyBytes, 1, request, limits);
        }
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < iterations; i++)
        {
            RequestCodec.Encode(destination, 1, request, limits);
        }
        var encoding = GC.GetAllocatedBytesForCurrentThread() - before;
        before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < iterations; i++)
        {
            last = ReplyCodec.DecodeForValidatedRequest(replyBytes, 1, request, limits);
        }
        var decoding = GC.GetAllocatedBytesForCurrentThread() - before;
        GC.KeepAlive(last);
        var encodedPerOperation = encoding / (double)iterations;
        var decodedPerOperation = decoding / (double)iterations;
        output.WriteLine(
            $"get-data encode: {encodedPerOperation} bytes/op; decode: {decodedPerOperation} bytes/op; iterations: {iterations}"
        );

        var path = Environment.GetEnvironmentVariable("ZKLINK_ALLOCATION_OUTPUT");
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
