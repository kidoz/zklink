using System.Net;
using System.Net.Sockets;

namespace ZkLink.IntegrationTests;

public sealed class WatchReconnectProxyTests
{
    private const int TestDeadlineMilliseconds = 15_000;
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(5);

    [Fact(Timeout = TestDeadlineMilliseconds)]
    public async Task OpaqueIncomingHoldPreservesOutgoingProgressAndReleasesExactBytes()
    {
        var token = TestContext.Current.CancellationToken;
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var proxy = new WatchReconnectProxy(
            $"127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}",
            false,
            token
        );
        await using var lifetime = proxy.ConfigureAwait(true);
        using var client = new TcpClient();
        var endpoint = new Uri("tcp://" + proxy.Endpoint);
        await client.ConnectAsync(endpoint.Host, endpoint.Port, token).ConfigureAwait(true);
        using var upstream = await listener.AcceptTcpClientAsync(token).ConfigureAwait(true);
        proxy.HoldIncoming();
        byte[] reply = "opaque-server-reply"u8.ToArray();
        await upstream.GetStream().WriteAsync(reply, token).ConfigureAwait(true);
        await proxy.IncomingHeld.WaitAsync(Guard, token).ConfigureAwait(true);
        var received = new byte[reply.Length];
        var read = client.GetStream().ReadExactlyAsync(received, token).AsTask();
        Assert.False(read.IsCompleted);
        byte[] request = "outgoing-progress"u8.ToArray();
        await client.GetStream().WriteAsync(request, token).ConfigureAwait(true);
        var forwarded = new byte[request.Length];
        await upstream.GetStream().ReadExactlyAsync(forwarded, token).ConfigureAwait(true);
        Assert.Equal(request, forwarded);
        Assert.False(read.IsCompleted);
        proxy.ReleaseIncoming();
        await read.WaitAsync(Guard, token).ConfigureAwait(true);
        Assert.Equal(reply, received);
    }
}
