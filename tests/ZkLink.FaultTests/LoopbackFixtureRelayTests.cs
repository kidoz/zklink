using System.Net;
using System.Net.Sockets;

namespace ZkLink.FaultTests;

[Trait("Fixture", "ProxyUnit")]
public sealed class LoopbackFixtureRelayTests
{
    [Fact(Timeout = 10000)]
    [Trait("Acceptance", "T33")]
    public async Task MaintenanceRelayForwardsBothDirectionsReconnectsAndClosesOwnedSockets()
    {
        var token = TestContext.Current.CancellationToken;
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var relay = new LoopbackFixtureRelay(listener.LocalEndpoint.ToString()!, token);
        await using var lifetime = relay.ConfigureAwait(true);
        for (int attempt = 0; attempt < 2; attempt++)
        {
            using var client = new TcpClient();
            await client.ConnectAsync(IPEndPoint.Parse(relay.Endpoint), token).ConfigureAwait(true);
            using var upstream = await listener.AcceptTcpClientAsync(token).ConfigureAwait(true);
            byte[] request = "maintenance-request"u8.ToArray();
            await client.GetStream().WriteAsync(request, token).ConfigureAwait(true);
            var forwarded = new byte[request.Length];
            await upstream.GetStream().ReadExactlyAsync(forwarded, token).ConfigureAwait(true);
            Assert.Equal(request, forwarded);
            byte[] reply = "maintenance-reply"u8.ToArray();
            await upstream.GetStream().WriteAsync(reply, token).ConfigureAwait(true);
            var received = new byte[reply.Length];
            await client.GetStream().ReadExactlyAsync(received, token).ConfigureAwait(true);
            Assert.Equal(reply, received);
        }
    }

    [Fact(Timeout = 10000)]
    public async Task DisposalClosesAnActiveMaintenanceConnectionAndListener()
    {
        var token = TestContext.Current.CancellationToken;
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var relay = new LoopbackFixtureRelay(listener.LocalEndpoint.ToString()!, token);
        await using var lifetime = relay.ConfigureAwait(true);
        using var client = new TcpClient();
        using var upstream = await ConnectAsync().ConfigureAwait(true);
        await relay.DisposeAsync().ConfigureAwait(true);
        Assert.Equal(
            0,
            await client.GetStream().ReadAsync(new byte[1], token).ConfigureAwait(true)
        );
        Assert.Equal(
            0,
            await upstream.GetStream().ReadAsync(new byte[1], token).ConfigureAwait(true)
        );
        using var later = new TcpClient();
        await Assert
            .ThrowsAsync<SocketException>(() =>
                later.ConnectAsync(IPEndPoint.Parse(relay.Endpoint), token).AsTask()
            )
            .ConfigureAwait(true);

        async Task<TcpClient> ConnectAsync()
        {
            await client.ConnectAsync(IPEndPoint.Parse(relay.Endpoint), token).ConfigureAwait(true);
            return await listener.AcceptTcpClientAsync(token).ConfigureAwait(true);
        }
    }
}
