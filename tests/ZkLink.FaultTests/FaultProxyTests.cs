using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;

namespace ZkLink.FaultTests;

[Trait("Fixture", "ProxyUnit")]
public sealed class FaultProxyTests
{
    private const int TestTimeoutMilliseconds = 15_000;

    [Theory(Timeout = TestTimeoutMilliseconds)]
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

    [Fact(Timeout = TestTimeoutMilliseconds)]
    public async Task ConnectionTimelineIsBoundedAndSnapshotsDoNotAliasLaterEvents()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var proxy = new FaultProxy(
            $"127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}",
            false,
            TestContext.Current.CancellationToken
        );
        await using var lifetime = proxy.ConfigureAwait(true);
        var before = DateTimeOffset.UtcNow;
        var empty = proxy.ConnectionSnapshot();
        for (int index = 0; index <= FaultProxy.MaximumConnectionEvents; index++)
        {
            Assert.Equal(0, proxy.Checkpoint(ProxyConnectionEventKind.ReadyCheckpoint));
        }
        var snapshot = proxy.ConnectionSnapshot();
        Assert.Empty(empty.Events);
        Assert.Equal(FaultProxy.MaximumConnectionEvents, snapshot.Events.Count);
        Assert.Equal(1, snapshot.DroppedEvents);
        Assert.All(
            snapshot.Events,
            item =>
            {
                Assert.InRange(item.ObservedAtUtc, before, DateTimeOffset.UtcNow);
                Assert.Equal(ProxyConnectionEventKind.ReadyCheckpoint, item.Kind);
                Assert.Null(item.SocketError);
                Assert.Null(item.Incoming);
            }
        );
    }

    [Fact(Timeout = TestTimeoutMilliseconds)]
    public async Task WithheldCreateRetainsItsCommitZxidForIndependentWriteFencing()
    {
        const int Xid = 41;
        const long CommitZxid = 12345;
        const int PrefixBytes = sizeof(int);
        const int ReplyHeaderBytes = sizeof(int) + sizeof(long) + sizeof(int);
        var token = TestContext.Current.CancellationToken;
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var proxy = new FaultProxy(
            $"127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}",
            false,
            token
        );
        await using var lifetime = proxy.ConfigureAwait(true);
        using var client = new TcpClient();
        var endpoint = new Uri("tcp://" + proxy.Endpoint);
        await client.ConnectAsync(endpoint.Host, endpoint.Port, token).ConfigureAwait(true);
        using var upstream = await listener.AcceptTcpClientAsync(token).ConfigureAwait(true);
        var handshake = Frame();
        var received = new byte[handshake.Length];
        await client.GetStream().WriteAsync(handshake, token).ConfigureAwait(true);
        await upstream.GetStream().ReadExactlyAsync(received, token).ConfigureAwait(true);
        await upstream.GetStream().WriteAsync(handshake, token).ConfigureAwait(true);
        await client.GetStream().ReadExactlyAsync(received, token).ConfigureAwait(true);
        proxy.ArmCreateReply();
        var request = new byte[PrefixBytes + ReplyHeaderBytes];
        BinaryPrimitives.WriteInt32BigEndian(request, ReplyHeaderBytes);
        BinaryPrimitives.WriteInt32BigEndian(request.AsSpan(PrefixBytes), Xid);
        BinaryPrimitives.WriteInt32BigEndian(
            request.AsSpan(PrefixBytes + sizeof(int)),
            (int)ZooKeeperOperation.CreateWithStat
        );
        await client.GetStream().WriteAsync(request, token).ConfigureAwait(true);
        await upstream
            .GetStream()
            .ReadExactlyAsync(new byte[request.Length], token)
            .ConfigureAwait(true);
        var reply = new byte[PrefixBytes + ReplyHeaderBytes];
        BinaryPrimitives.WriteInt32BigEndian(reply, ReplyHeaderBytes);
        BinaryPrimitives.WriteInt32BigEndian(reply.AsSpan(PrefixBytes), Xid);
        BinaryPrimitives.WriteInt64BigEndian(reply.AsSpan(PrefixBytes + sizeof(int)), CommitZxid);
        await upstream.GetStream().WriteAsync(reply, token).ConfigureAwait(true);
        await proxy.ResponseWithheld.WaitAsync(TimeSpan.FromSeconds(5), token).ConfigureAwait(true);
        Assert.Equal(FaultProxy.Success, proxy.WithheldError);
        Assert.Equal(CommitZxid, proxy.WithheldZxid);
        Assert.Equal(1, proxy.CreateTransmissions);
    }

    public static TheoryData<bool, bool, int> RejectedFrames
    {
        get
        {
            var cases = new TheoryData<bool, bool, int>();
            foreach (bool incoming in new[] { false, true })
            {
                foreach (bool handshake in new[] { false, true })
                {
                    foreach (
                        int length in new[]
                        {
                            -1,
                            0,
                            FaultProxy.MaximumFrameBytes + 1,
                            int.MaxValue,
                        }
                    )
                    {
                        cases.Add(incoming, handshake, length);
                    }
                }
            }
            return cases;
        }
    }

    [Theory(Timeout = TestTimeoutMilliseconds)]
    [MemberData(nameof(RejectedFrames))]
    public async Task RejectedPrefixRetainsSafeMetadataBeforeDelayedDisposal(
        bool incoming,
        bool handshake,
        int length
    )
    {
        var token = TestContext.Current.CancellationToken;
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        Exception? failure = null;
        var before = DateTimeOffset.UtcNow;
        var observedBy = before;
        try
        {
            var proxy = new FaultProxy(
                $"127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}",
                false,
                token
            );
            await using var lifetime = proxy.ConfigureAwait(true);
            using var client = new TcpClient();
            var endpoint = new Uri("tcp://" + proxy.Endpoint);
            await client.ConnectAsync(endpoint.Host, endpoint.Port, token).ConfigureAwait(true);
            using var upstream = await listener.AcceptTcpClientAsync(token).ConfigureAwait(true);
            var sender = incoming ? upstream.GetStream() : client.GetStream();
            var receiver = incoming ? client.GetStream() : upstream.GetStream();
            if (!handshake)
            {
                var first = Frame();
                await sender.WriteAsync(first, token).ConfigureAwait(true);
                var received = new byte[first.Length];
                await receiver.ReadExactlyAsync(received, token).ConfigureAwait(true);
                Assert.Equal(first, received);
            }
            var prefix = new byte[sizeof(int)];
            BinaryPrimitives.WriteInt32BigEndian(prefix, length);
            await sender.WriteAsync(prefix, token).ConfigureAwait(true);
            Assert.Equal(0, await receiver.ReadAsync(new byte[1], token).ConfigureAwait(true));
            observedBy = DateTimeOffset.UtcNow;
        }
        catch (InvalidDataException error)
        {
            failure = error;
        }
        var rejected = Assert.IsType<InvalidDataException>(failure);
        var detail = Assert.IsType<FaultProxyFrameFailure>(
            rejected.Data[typeof(FaultProxyFrameFailure)]
        );
        Assert.InRange(detail.ObservedAtUtc, before, observedBy);
        Assert.Equal(incoming, detail.Incoming);
        Assert.Equal(handshake, detail.Handshake);
        Assert.Equal(1, detail.ConnectionAttempt);
        Assert.Equal(handshake ? 1 : 2, detail.FrameNumber);
        Assert.Equal(length, detail.Length);
        Assert.Equal(FaultProxy.MaximumFrameBytes, detail.MaximumLength);
    }

    [Theory(Timeout = TestTimeoutMilliseconds)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FragmentedAndCoalescedFramesForwardWithoutFalseRejection(bool incoming)
    {
        var token = TestContext.Current.CancellationToken;
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var proxy = new FaultProxy(
            $"127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}",
            false,
            token
        );
        await using var lifetime = proxy.ConfigureAwait(true);
        using var client = new TcpClient();
        var endpoint = new Uri("tcp://" + proxy.Endpoint);
        await client.ConnectAsync(endpoint.Host, endpoint.Port, token).ConfigureAwait(true);
        using var upstream = await listener.AcceptTcpClientAsync(token).ConfigureAwait(true);
        var sender = incoming ? upstream.GetStream() : client.GetStream();
        var receiver = incoming ? client.GetStream() : upstream.GetStream();
        var frame = Frame();
        for (int index = 0; index < frame.Length; index++)
        {
            await sender.WriteAsync(frame.AsMemory(index, 1), token).ConfigureAwait(true);
        }
        var received = new byte[frame.Length];
        await receiver.ReadExactlyAsync(received, token).ConfigureAwait(true);
        Assert.Equal(frame, received);
        byte[] coalesced = [.. frame, .. frame];
        await sender.WriteAsync(coalesced, token).ConfigureAwait(true);
        var receivedTogether = new byte[coalesced.Length];
        await receiver.ReadExactlyAsync(receivedTogether, token).ConfigureAwait(true);
        Assert.Equal(coalesced, receivedTogether);
        Assert.Null(proxy.RunFailure);
    }

    public static TheoryData<bool, int> TruncatedFrames
    {
        get
        {
            var cases = new TheoryData<bool, int>();
            foreach (bool incoming in new[] { false, true })
            {
                for (int sentBytes = 1; sentBytes < Frame().Length; sentBytes++)
                {
                    cases.Add(incoming, sentBytes);
                }
            }
            return cases;
        }
    }

    [Theory(Timeout = TestTimeoutMilliseconds)]
    [MemberData(nameof(TruncatedFrames))]
    public async Task TruncatedFrameAfterHandshakeAllowsCleanReconnect(bool incoming, int sentBytes)
    {
        var token = TestContext.Current.CancellationToken;
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var proxy = new FaultProxy(
            $"127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}",
            false,
            token
        );
        await using var lifetime = proxy.ConfigureAwait(true);
        var endpoint = new Uri("tcp://" + proxy.Endpoint);
        var frame = Frame();
        var received = new byte[frame.Length];
        using (var client = new TcpClient())
        {
            await client.ConnectAsync(endpoint.Host, endpoint.Port, token).ConfigureAwait(true);
            using var upstream = await listener.AcceptTcpClientAsync(token).ConfigureAwait(true);
            await client.GetStream().WriteAsync(frame, token).ConfigureAwait(true);
            await upstream.GetStream().ReadExactlyAsync(received, token).ConfigureAwait(true);
            Assert.Equal(frame, received);
            await upstream.GetStream().WriteAsync(frame, token).ConfigureAwait(true);
            await client.GetStream().ReadExactlyAsync(received, token).ConfigureAwait(true);
            Assert.Equal(frame, received);

            var sender = incoming ? upstream : client;
            var receiver = incoming ? client : upstream;
            await sender
                .GetStream()
                .WriteAsync(frame.AsMemory(0, sentBytes), token)
                .ConfigureAwait(true);
            sender.Client.Shutdown(SocketShutdown.Send);
            // The proxy buffers complete frames: no partial prefix or body may escape.
            Assert.Equal(
                0,
                await receiver.GetStream().ReadAsync(new byte[1], token).ConfigureAwait(true)
            );
        }

        using var finalClient = new TcpClient();
        await finalClient.ConnectAsync(endpoint.Host, endpoint.Port, token).ConfigureAwait(true);
        using var finalUpstream = await listener.AcceptTcpClientAsync(token).ConfigureAwait(true);
        await finalClient.GetStream().WriteAsync(frame, token).ConfigureAwait(true);
        await finalUpstream.GetStream().ReadExactlyAsync(received, token).ConfigureAwait(true);
        Assert.Equal(frame, received);
        await finalUpstream.GetStream().WriteAsync(frame, token).ConfigureAwait(true);
        await finalClient.GetStream().ReadExactlyAsync(received, token).ConfigureAwait(true);
        Assert.Equal(frame, received);
        Assert.Equal(2, proxy.ConnectionAttempts);
        Assert.Equal(2, proxy.HandshakeReplies);
        Assert.Null(proxy.RunFailure);
    }

    private static byte[] Frame()
    {
        var frame = new byte[3 * sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(frame, 2 * sizeof(int));
        return frame;
    }
}
