using System.Net;
using System.Net.Sockets;

namespace ZkLink.FaultTests;

[Collection("WatchPressure")]
[Trait("Fixture", "SoakUnit")]
public sealed class SoakEvidenceTests
{
    private const int TestTimeoutMilliseconds = 30_000;
    private const int RetainedAllocationBytes = 32 * 1024 * 1024;
    private const int DurationSeconds = 30;
    private const int Seed = 1729;

    [Fact(Timeout = TestTimeoutMilliseconds)]
    public async Task RetainedAllocationBreachesTheRecordedHeapBound()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "zklink-soak-unit-" + Guid.NewGuid().ToString("N")
        );
        Directory.CreateDirectory(directory);
        try
        {
            var evidence = new SoakEvidence(
                new SoakSettings(DurationSeconds, Seed, Path.Combine(directory, "samples.jsonl"))
            );
            await using var lifetime = evidence.ConfigureAwait(true);
            var totals = new SoakTotals();
            await evidence
                .SampleAsync(
                    "baseline",
                    totals,
                    TimeSpan.Zero,
                    TestContext.Current.CancellationToken
                )
                .ConfigureAwait(true);
            var retained = new byte[RetainedAllocationBytes];
            var failure = await Record
                .ExceptionAsync(() =>
                    evidence.SampleAsync(
                        "checkpoint",
                        totals,
                        TimeSpan.FromSeconds(1),
                        TestContext.Current.CancellationToken
                    )
                )
                .ConfigureAwait(true);
            GC.KeepAlive(retained);
            Assert.NotNull(failure);
            Assert.Contains(
                "Retained managed heap exceeded",
                failure.Message,
                StringComparison.Ordinal
            );
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact(Timeout = TestTimeoutMilliseconds)]
    public async Task SocketObservationWaitsForOwnedConnectionAndIgnoresAnotherDestination()
    {
        var token = TestContext.Current.CancellationToken;
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        using var unrelated = new TcpListener(IPAddress.Loopback, 0);
        using var client = new TcpClient();
        listener.Start();
        unrelated.Start();
        int target = ((IPEndPoint)listener.LocalEndpoint).Port;
        int otherTarget = ((IPEndPoint)unrelated.LocalEndpoint).Port;
        await client.ConnectAsync(IPAddress.Loopback, target, token).ConfigureAwait(true);
        using var accepted = await listener.AcceptTcpClientAsync(token).ConfigureAwait(true);
        int local = ((IPEndPoint)client.Client.LocalEndPoint!).Port;
        var closed = SoakMetrics.PortsClosedAsync([], [local], target, token);
        try
        {
            Assert.False(closed.IsCompleted);
            await SoakMetrics
                .PortsClosedAsync([], [local], otherTarget, token)
                .ConfigureAwait(true);
        }
        finally
        {
            client.Dispose();
            accepted.Dispose();
            listener.Stop();
            await closed.ConfigureAwait(true);
        }
    }
}
