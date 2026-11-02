using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;

namespace ZkLink.FaultTests;

[Collection("WatchPressure")]
[Trait("Fixture", "SoakUnit")]
public sealed class SoakSocketOwnershipTests
{
    private const int TestTimeoutMilliseconds = 30_000;
    private static readonly TimeSpan ClosureGuard = TimeSpan.FromSeconds(2);

    [Fact(Timeout = TestTimeoutMilliseconds)]
    public async Task OtherLoopbackAddressWithSameListenerPortDoesNotBlock()
    {
        using var original = new TcpListener(IPAddress.Loopback, 0);
        original.Start();
        int port = ((IPEndPoint)original.LocalEndpoint).Port;
        var otherAddress = OperatingSystem.IsLinux()
            ? IPAddress.Parse("127.0.0.2")
            : IPAddress.IPv6Loopback;
        using var other = new TcpListener(otherAddress, port);
        if (otherAddress.AddressFamily == AddressFamily.InterNetworkV6)
        {
            other.Server.DualMode = false;
        }
        other.Start();
        var sockets = new SoakSocketScope();
        sockets.TrackListener(original.Server);
        original.Stop();
        // This is precisely the old port-only rule, with a live foreign endpoint.
        Assert.Contains(
            IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners(),
            endpoint => endpoint.Port == port && IPAddress.IsLoopback(endpoint.Address)
        );
        using var closure = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken
        );
        closure.CancelAfter(ClosureGuard);
        await SoakMetrics.PortsClosedAsync(sockets, closure.Token).ConfigureAwait(true);
    }

    [Fact(Timeout = TestTimeoutMilliseconds)]
    public async Task RetainedOriginalListenerStillTimesOutWithSafeEvidence()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var sockets = new SoakSocketScope();
        sockets.TrackListener(listener.Server);
        var before = DateTimeOffset.UtcNow;
        var failure = await Assert
            .ThrowsAsync<TimeoutException>(() =>
                SoakDiagnostics.ObserveAsync(
                    SoakMetrics.PortsClosedAsync(sockets, TestContext.Current.CancellationToken),
                    SoakWaitSite.ProxyConnectionsClosed
                )
            )
            .ConfigureAwait(true);
        Assert.Equal(SoakWaitSite.ProxyConnectionsClosed, failure.Data[typeof(SoakWaitSite)]);
        var snapshot = Assert.IsType<SoakSocketSnapshot>(failure.Data[typeof(SoakSocketSnapshot)]);
        Assert.Equal(1, snapshot.OpenOwnedHandles);
        Assert.InRange(snapshot.ObservedAtUtc, before, DateTimeOffset.UtcNow);
        Assert.Contains(snapshot.Matches, row => row.State == TcpState.Listen);
        using var json = JsonDocument.Parse(snapshot.ToJson());
        Assert.Equal(1, json.RootElement.GetProperty("open_owned_handles").GetInt32());
    }

    [Fact(Timeout = TestTimeoutMilliseconds)]
    public async Task RetainedPeerHalfFromAnEarlierConnectionIsStillObserved()
    {
        var token = TestContext.Current.CancellationToken;
        using var server = new TcpListener(IPAddress.Loopback, 0);
        server.Start();
        var endpoint = (IPEndPoint)server.LocalEndpoint;
        using var first = new TcpClient();
        using var second = new TcpClient();
        await first.ConnectAsync(endpoint, token).ConfigureAwait(true);
        using var peer = await server.AcceptTcpClientAsync(token).ConfigureAwait(true);
        await second.ConnectAsync(endpoint, token).ConfigureAwait(true);
        using var laterPeer = await server.AcceptTcpClientAsync(token).ConfigureAwait(true);
        var sockets = new SoakSocketScope();
        sockets.TrackConnection(first.Client);
        sockets.TrackConnection(second.Client);
        var originalPeerEndpoint = (IPEndPoint)first.Client.LocalEndPoint!;
        first.Dispose();
        second.Dispose();
        laterPeer.Dispose();
        await SoakMetrics
            .UntilAsync(
                () =>
                    IPGlobalProperties
                        .GetIPGlobalProperties()
                        .GetActiveTcpConnections()
                        .Any(connection =>
                            SoakSocketInventory.SameEndpoint(connection.LocalEndPoint, endpoint)
                            && SoakSocketInventory.SameEndpoint(
                                connection.RemoteEndPoint,
                                originalPeerEndpoint
                            )
                            && connection.State == TcpState.CloseWait
                        ),
                "The retained test peer did not reach CLOSE_WAIT.",
                token
            )
            .ConfigureAwait(true);
        var wait = SoakMetrics.PortsClosedAsync(sockets, token);
        try
        {
            Assert.False(wait.IsCompleted);
        }
        finally
        {
            peer.Dispose();
            await wait.ConfigureAwait(true);
        }
    }

    [Fact(Timeout = TestTimeoutMilliseconds)]
    public async Task ConcurrentFaultStopAndFinalDisposeCloseOriginalListenerOnce()
    {
        var token = TestContext.Current.CancellationToken;
        using var target = new TcpListener(IPAddress.Loopback, 0);
        target.Start();
        var sockets = new SoakSocketScope();
        var proxy = new FaultProxy(target.LocalEndpoint.ToString()!, false, token, sockets);
        await using var lifetime = proxy.ConfigureAwait(true);
        var endpoint = IPEndPoint.Parse(proxy.Endpoint);
        await Task.WhenAll(proxy.StopForwardingAsync().AsTask(), proxy.DisposeAsync().AsTask())
            .ConfigureAwait(true);
        await proxy.DisposeAsync().ConfigureAwait(true);
        Assert.Single(
            proxy.ConnectionSnapshot().Events,
            item => item.Kind == ProxyConnectionEventKind.StopRequested
        );
        await SoakMetrics.PortsClosedAsync(sockets, token).ConfigureAwait(true);
        using var replacement = new TcpListener(endpoint);
        replacement.Start();
    }

    [Fact]
    public void IPv4MappedAddressesMatchOnlyTheSameEndpoint()
    {
        const int Port = 2181;
        var plain = new IPEndPoint(IPAddress.Loopback, Port);
        var mapped = new IPEndPoint(IPAddress.Loopback.MapToIPv6(), Port);
        var other = new IPEndPoint(IPAddress.Parse("127.0.0.11"), Port);
        Assert.True(SoakSocketInventory.SameEndpoint(plain, mapped));
        Assert.False(SoakSocketInventory.SameEndpoint(plain, other));
    }
}
