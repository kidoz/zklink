using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace ZkLink.FaultTests;

[Trait("Fixture", "SocketNamespace")]
public sealed class SoakSocketNamespaceTests
{
    private const int TestTimeoutMilliseconds = 30_000;

    [Fact(Timeout = TestTimeoutMilliseconds)]
    public async Task DockerNamespaceDnsListenerDoesNotBecomeAnOwnedProxyListener()
    {
        Assert.True(
            OperatingSystem.IsLinux(),
            "Run inside the owned Linux Docker network fixture."
        );
        const string Resolver = "nameserver 127.0.0.11";
        Assert.True(
            (
                await File.ReadAllLinesAsync(
                        "/etc/resolv.conf",
                        TestContext.Current.CancellationToken
                    )
                    .ConfigureAwait(true)
            ).Any(line => line.Trim() == Resolver),
            "Missing owned Docker DNS fixture."
        );
        var address = IPAddress.Parse("127.0.0.11");
        var dns = Assert.Single(
            IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners(),
            endpoint => endpoint.Address.Equals(address)
        );
        using var proxy = new TcpListener(IPAddress.Loopback, dns.Port);
        proxy.Start();
        var sockets = new SoakSocketScope();
        sockets.TrackListener(proxy.Server);
        proxy.Stop();
        Assert.Contains(
            IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners(),
            endpoint => endpoint.Equals(dns)
        );
        using var closure = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken
        );
        closure.CancelAfter(TimeSpan.FromSeconds(2));
        await SoakMetrics.PortsClosedAsync(sockets, closure.Token).ConfigureAwait(true);
        TestContext.Current.TestOutputHelper?.WriteLine(
            "namespace-listener-collision: PASS original-handle-closed=true foreign-dns-listener-retained=true"
        );
    }
}
