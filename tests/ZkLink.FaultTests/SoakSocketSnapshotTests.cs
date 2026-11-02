using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;

namespace ZkLink.FaultTests;

[Collection("WatchPressure")]
[Trait("Fixture", "SoakUnit")]
public sealed class SoakSocketSnapshotTests
{
    private const int TestTimeoutMilliseconds = 30_000;

    [Theory]
    [InlineData("0100007F:1234", "127.0.0.1")]
    [InlineData("00000000000000000000000001000000:1234", "::1")]
    public void ParsesKernelEndpointWords(string value, string address)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(address);
        var parsed = SoakSocketSnapshot.ParseEndpoint(value);
        Assert.Equal(IPAddress.Parse(address), parsed.Address);
        Assert.Equal(0x1234, parsed.Port);
    }

    [Fact(Timeout = TestTimeoutMilliseconds)]
    public async Task SnapshotCapturesOnlyScopedTuplesAndReportsProcessOwnership()
    {
        var token = TestContext.Current.CancellationToken;
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        using var unrelated = new TcpListener(IPAddress.Loopback, 0);
        using var client = new TcpClient();
        listener.Start();
        unrelated.Start();
        int target = ((IPEndPoint)listener.LocalEndpoint).Port;
        int other = ((IPEndPoint)unrelated.LocalEndpoint).Port;
        await client.ConnectAsync(IPAddress.Loopback, target, token).ConfigureAwait(true);
        using var accepted = await listener.AcceptTcpClientAsync(token).ConfigureAwait(true);
        int local = ((IPEndPoint)client.Client.LocalEndPoint!).Port;
        var sockets = new SoakSocketScope();
        sockets.TrackListener(listener.Server);
        sockets.TrackConnection(client.Client);
        var snapshot = SoakSocketSnapshot.Capture(sockets.Seal());
        Assert.DoesNotContain(
            snapshot.Matches,
            row => row.LocalPort == other || row.RemotePort == other
        );
        var connection = Assert.Single(
            snapshot.Matches,
            row => row.LocalPort == local && row.RemotePort == target
        );
        Assert.Equal(TcpState.Established, connection.State);
        Assert.Contains(
            snapshot.Matches,
            row => row.LocalPort == target && row.State == TcpState.Listen
        );
        if (OperatingSystem.IsLinux())
        {
            Assert.Equal("Captured", snapshot.OwnershipStatus);
            Assert.True(connection.OwnedByTestProcess);
            Assert.True(connection.Inode > 0);
        }
        else
        {
            Assert.Equal("Unsupported", snapshot.OwnershipStatus);
            Assert.Null(connection.OwnedByTestProcess);
        }
        string json = snapshot.ToJson();
        Assert.DoesNotContain("127.0.0.1", json, StringComparison.Ordinal);
        using var document = JsonDocument.Parse(json);
        Assert.Equal(
            snapshot.TotalMatches,
            document.RootElement.GetProperty("total_matches").GetInt32()
        );
    }

    [Fact(Timeout = TestTimeoutMilliseconds)]
    public async Task ClosedListenerPortReusedByAnUnrelatedConnectionDoesNotBlock()
    {
        var token = TestContext.Current.CancellationToken;
        using var original = new TcpListener(IPAddress.Loopback, 0);
        using var target = new TcpListener(IPAddress.Loopback, 0);
        using var other = new TcpListener(IPAddress.Loopback, 0);
        original.Start();
        target.Start();
        other.Start();
        var sockets = new SoakSocketScope();
        sockets.TrackListener(original.Server);
        int previousPort = ((IPEndPoint)original.LocalEndpoint).Port;
        int targetPort = ((IPEndPoint)target.LocalEndpoint).Port;
        int otherPort = ((IPEndPoint)other.LocalEndpoint).Port;
        original.Stop();
        using var client = new TcpClient(new IPEndPoint(IPAddress.Loopback, previousPort));
        await client.ConnectAsync(IPAddress.Loopback, otherPort, token).ConfigureAwait(true);
        using var accepted = await other.AcceptTcpClientAsync(token).ConfigureAwait(true);
        using var closure = CancellationTokenSource.CreateLinkedTokenSource(token);
        closure.CancelAfter(TimeSpan.FromSeconds(2));
        await SoakMetrics.PortsClosedAsync(sockets, closure.Token).ConfigureAwait(true);
        Assert.True(client.Connected);
        Assert.True(accepted.Connected);
    }
}
