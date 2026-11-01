using System.Buffers.Binary;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Text.Json;

namespace ZkLink.FaultTests;

[Trait("Fixture", "ProxyUnit")]
public sealed class FaultProxyAttributionTests
{
    private const int TestTimeoutMilliseconds = 15_000;
    private const int TlsHandshakeContentType = 22;
    private const int TlsLegacyMajorVersion = 3;
    private static readonly TimeSpan ObservationGuard = TimeSpan.FromSeconds(5);

    [Fact(Timeout = TestTimeoutMilliseconds)]
    public async Task UnownedTlsBeforeRestorationRemainsFatalAndRetainsLivePeerIdentity()
    {
        var token = TestContext.Current.CancellationToken;
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        Exception? check = null;
        InvalidDataException? primary = null;
        var disposal = await Record
            .ExceptionAsync(async () =>
            {
                var proxy = new FaultProxy(
                    $"127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}",
                    holdRestore: true,
                    token
                );
                await using var lifetime = proxy.ConfigureAwait(true);
                check = await Record
                    .ExceptionAsync(async () =>
                    {
                        using var foreign = new TcpClient();
                        var endpoint = new Uri("tcp://" + proxy.Endpoint);
                        await foreign
                            .ConnectAsync(endpoint.Host, endpoint.Port, token)
                            .ConfigureAwait(true);
                        int peerPort = ((IPEndPoint)foreign.Client.LocalEndPoint!).Port;
                        using var upstream = await listener
                            .AcceptTcpClientAsync(token)
                            .ConfigureAwait(true);
                        var tls = new SslStream(foreign.GetStream());
                        await using var tlsLifetime = tls.ConfigureAwait(true);
                        var before = DateTimeOffset.UtcNow;
                        Task handshake = tls.AuthenticateAsClientAsync(
                            new SslClientAuthenticationOptions
                            {
                                TargetHost = "zklink-attribution.test",
                            },
                            token
                        );
                        try
                        {
                            primary = await Assert
                                .ThrowsAsync<InvalidDataException>(() =>
                                    proxy.WaitForSignalAsync(
                                        proxy.RestoreHeld,
                                        ObservationGuard,
                                        token
                                    )
                                )
                                .ConfigureAwait(true);
                            var detail = Assert.IsType<FaultProxyFrameFailure>(
                                primary.Data[typeof(FaultProxyFrameFailure)]
                            );
                            Assert.InRange(detail.ObservedAtUtc, before, DateTimeOffset.UtcNow);
                            Assert.True(detail.Handshake);
                            Assert.False(detail.Incoming);
                            Assert.Equal(1, detail.FrameNumber);
                            var prefix = new byte[sizeof(int)];
                            BinaryPrimitives.WriteInt32BigEndian(prefix, detail.Length);
                            Assert.Equal(TlsHandshakeContentType, prefix[0]);
                            Assert.Equal(TlsLegacyMajorVersion, prefix[1]);
                            var identity = Assert.IsType<ProxyConnectionIdentity>(
                                detail.Connection
                            );
                            Assert.Equal(endpoint.Port, identity.ListenerPort);
                            Assert.Equal(peerPort, identity.PeerPort);
                            Assert.Equal(
                                ((IPEndPoint)upstream.Client.RemoteEndPoint!).Port,
                                identity.UpstreamLocalPort
                            );
                            Assert.Equal(
                                ((IPEndPoint)listener.LocalEndpoint).Port,
                                identity.TargetPort
                            );
                            var attribution = Assert.IsType<LoopbackPeerAttribution>(
                                detail.PeerAttribution
                            );
                            if (LoopbackPeerAttribution.Supported)
                            {
                                Assert.Equal(PeerAttributionStatus.Found, attribution.Status);
                                Assert.Contains(
                                    attribution.Processes,
                                    process => process.Id == Environment.ProcessId
                                );
                            }
                            else
                            {
                                Assert.Equal(PeerAttributionStatus.Unsupported, attribution.Status);
                            }
                            // No TLS body or partial prefix reaches ZooKeeper; this is
                            // still a fatal framing error, never an ignored probe.
                            Assert.Equal(
                                0,
                                await upstream
                                    .GetStream()
                                    .ReadAsync(new byte[1], token)
                                    .ConfigureAwait(true)
                            );
                            Assert.False(proxy.RestoreHeld.IsCompleted);
                            var accepted = Assert.Single(
                                proxy.ConnectionSnapshot().Events,
                                item => item.Kind == ProxyConnectionEventKind.Accepted
                            );
                            Assert.Equal(peerPort, accepted.Connection?.PeerPort);
                            Assert.Equal(endpoint.Port, proxy.ConnectionSnapshot().ListenerPort);
                            Assert.Equal(
                                identity,
                                Assert
                                    .Single(
                                        proxy.ConnectionSnapshot().Events,
                                        item =>
                                            item.Kind == ProxyConnectionEventKind.UpstreamConnected
                                    )
                                    .Connection
                            );
                        }
                        finally
                        {
                            var rejected = await Record
                                .ExceptionAsync(() => handshake.WaitAsync(ObservationGuard, token))
                                .ConfigureAwait(true);
                            Assert.NotNull(rejected);
                            Assert.IsNotType<TimeoutException>(rejected);
                        }
                    })
                    .ConfigureAwait(true);
            })
            .ConfigureAwait(true);
        Assert.Null(check);
        Assert.NotNull(primary);
        Assert.Same(primary, disposal);
    }

    [Fact]
    public void AttributionExcludesOtherSocketsAndUnrequestedFields()
    {
        const string PrivateMarker = "unrelated-private-marker";
        var connection = new ProxyConnectionIdentity(50001, 50002, 50003, 50004);
        string output =
            $"p100\nc{PrivateMarker}\nn127.0.0.1:60002->127.0.0.1:50001\np200\ncdotnet\na{PrivateMarker}\nn127.0.0.1:50001->127.0.0.1:50002\nn127.0.0.1:50002->127.0.0.1:50001\n";
        var attribution = LoopbackPeerAttribution.Parse(output, connection);
        Assert.Equal(PeerAttributionStatus.Found, attribution.Status);
        Assert.Equal(new LoopbackPeerProcess(200, "dotnet"), Assert.Single(attribution.Processes));
        Assert.DoesNotContain(PrivateMarker, attribution.ToJson(), StringComparison.Ordinal);
        using var json = JsonDocument.Parse(attribution.ToJson());
        Assert.Equal(
            200,
            json.RootElement.GetProperty("processes")[0].GetProperty("pid").GetInt32()
        );
    }
}
