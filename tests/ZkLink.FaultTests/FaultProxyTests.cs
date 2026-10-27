using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;

namespace ZkLink.FaultTests;

[Trait("Fixture", "ProxyUnit")]
public sealed class FaultProxyTests
{
    [Theory(Timeout = 15000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EarlyDisconnectsAllowFollowingConnectionToForwardFrames(bool upstreamCloses)
    {
        var token = TestContext.Current.CancellationToken;
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var proxy = new FaultProxy(
            $"127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}",
            holdRestore: false,
            token
        );
        await using var lifetime = proxy.ConfigureAwait(true);
        var endpoint = new Uri("tcp://" + proxy.Endpoint);
        for (int attempt = 0; attempt < 32; attempt++)
        {
            using var client = new TcpClient();
            await client.ConnectAsync(endpoint.Host, endpoint.Port, token).ConfigureAwait(true);
            if (!upstreamCloses)
            {
                client.Client.Shutdown(SocketShutdown.Both);
            }
            using var upstream = await listener.AcceptTcpClientAsync(token).ConfigureAwait(true);
            if (upstreamCloses)
            {
                upstream.Client.Shutdown(SocketShutdown.Both);
            }
            var reader = upstreamCloses ? client.GetStream() : upstream.GetStream();
            Assert.Equal(0, await reader.ReadAsync(new byte[1], token).ConfigureAwait(true));
        }

        using var finalClient = new TcpClient();
        await finalClient.ConnectAsync(endpoint.Host, endpoint.Port, token).ConfigureAwait(true);
        using var finalUpstream = await listener.AcceptTcpClientAsync(token).ConfigureAwait(true);
        var packet = new byte[3 * sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(packet, 2 * sizeof(int));
        BinaryPrimitives.WriteInt32BigEndian(packet.AsSpan(2 * sizeof(int)), 6000);
        await finalClient.GetStream().WriteAsync(packet, token).ConfigureAwait(true);
        var received = new byte[packet.Length];
        await finalUpstream.GetStream().ReadExactlyAsync(received, token).ConfigureAwait(true);
        Assert.Equal(packet, received);
        await finalUpstream.GetStream().WriteAsync(packet, token).ConfigureAwait(true);
        await finalClient.GetStream().ReadExactlyAsync(received, token).ConfigureAwait(true);
        Assert.Equal(packet, received);
        Assert.Equal(33, proxy.ConnectionAttempts);
        Assert.Equal(1, proxy.HandshakeReplies);
        Assert.Null(proxy.RunFailure);
        // Proxy disposal also has to observe both blocked forwarding tasks.
    }
}
