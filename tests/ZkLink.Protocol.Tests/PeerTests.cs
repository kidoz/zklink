using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using ZkLink.Protocol;
using ZkLink.Protocol.Tests.Peers;

namespace ZkLink.Protocol.Tests;

public sealed class PeerTests
{
    public static IEnumerable<object[]> Names => ProtocolGoldenTests.Names;

    internal static byte[] Frame(byte[] payload)
    {
        var frame = new byte[payload.Length + 4];
        BinaryPrimitives.WriteInt32BigEndian(frame, payload.Length);
        payload.CopyTo(frame, 4);
        return frame;
    }

    [Theory]
    [MemberData(nameof(Names))]
    [Trait("Acceptance", "T02")]
    public async Task EveryReplyShapeSurvivesEveryStreamSplit(string name)
    {
        var fixture = ProtocolFixtures.All.Single(item => item.Name == name);
        var frame = Frame(ProtocolFixtures.Bytes(name + "-reply"));
        for (var split = 1; split < frame.Length; split++)
        {
            using var peer = new ScriptedPeerStream();
            peer.Enqueue(frame[..split], frame[split..]);
            peer.CompleteInput();
            var reader = new FrameReader(peer, ProtocolFixtures.Limits);
            var payload = Assert.IsType<byte[]>(
                await reader.ReadAsync(TestContext.Current.CancellationToken)
            );
            ProtocolFixtures.AssertResult(
                fixture.Result,
                ReplyCodec
                    .Decode(payload, fixture.Xid, fixture.Request, ProtocolFixtures.Limits)
                    .Result
            );
            Assert.Null(await reader.ReadAsync(TestContext.Current.CancellationToken));
            Assert.Equal(0, peer.PendingBytes);
        }
    }

    [Fact]
    public async Task ReplyAndNotificationInOneChunkRemainSeparateAndOrdered()
    {
        using var peer = new ScriptedPeerStream();
        peer.Enqueue([
            .. Frame(ProtocolFixtures.Bytes("get-data-reply")),
            .. Frame(ProtocolFixtures.Bytes("notification")),
        ]);
        peer.CompleteInput();
        var reader = new FrameReader(peer, ProtocolFixtures.Limits);
        var first = Assert.IsType<byte[]>(
            await reader.ReadAsync(TestContext.Current.CancellationToken)
        );
        var second = Assert.IsType<byte[]>(
            await reader.ReadAsync(TestContext.Current.CancellationToken)
        );
        Assert.IsType<DataResult>(
            ReplyCodec.Decode(first, 1, ProtocolFixtures.Reads[0], ProtocolFixtures.Limits).Result
        );
        Assert.Equal(3, ReplyCodec.DecodeNotification(second, ProtocolFixtures.Limits).Type);
        Assert.Null(await reader.ReadAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task DelayedDeliveryCancellationAndPartialWritesNeedNoSleeps()
    {
        using var peer = new ScriptedPeerStream();
        var reader = new FrameReader(peer, ProtocolFixtures.Limits);
        var delayed = reader.ReadAsync(TestContext.Current.CancellationToken);
        Assert.False(delayed.IsCompleted);
        peer.Enqueue(Frame(ProtocolFixtures.Bytes("ping-reply")));
        Assert.NotNull(await delayed.ConfigureAwait(true));
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken
        );
        var cancelled = reader.ReadAsync(cancellation.Token);
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);

        using var failingPeer = new ScriptedPeerStream { FailWriteAfterBytes = 7 };
        var writer = new FrameWriter(failingPeer, ProtocolFixtures.Limits);
        var request = ProtocolFixtures.Bytes("get-data-request");
        await Assert.ThrowsAsync<IOException>(() =>
            writer.WriteAsync(request, TestContext.Current.CancellationToken)
        );
        Assert.Equal(Frame(request)[..7], failingPeer.WrittenBytes);
    }

    [Fact]
    public async Task LoopbackPeerChecksRequestAndCanDropAPartialResponse()
    {
        var limits = ProtocolFixtures.Limits;
        var expected = ProtocolFixtures.Bytes("get-data-request");
        var server = new FakeZooKeeperServer(
            async (stream, token) =>
            {
                var reader = new FrameReader(stream, limits);
                Assert.Equal(expected, await reader.ReadAsync(token).ConfigureAwait(true));
                var frame = Frame(ProtocolFixtures.Bytes("get-data-reply"));
                await stream.WriteAsync(frame.AsMemory(0, 7), token).ConfigureAwait(true);
                // Normal socket disposal ends this deliberately incomplete frame.
            },
            TestContext.Current.CancellationToken
        );
        await using var lifetime = server.ConfigureAwait(true);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken
        );
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, server.Port, deadline.Token);
        var writer = new FrameWriter(client.GetStream(), limits);
        await writer.WriteAsync(expected, deadline.Token);
        var replyReader = new FrameReader(client.GetStream(), limits);
        await Assert.ThrowsAsync<EndOfStreamException>(() => replyReader.ReadAsync(deadline.Token));
        await server.Completion.ConfigureAwait(true);
    }

    [Fact]
    public async Task LoopbackPeerExchangesHandshakeThenApplicationPacket()
    {
        var limits = ProtocolFixtures.Limits;
        var server = new FakeZooKeeperServer(
            async (stream, token) =>
            {
                var reader = new FrameReader(stream, limits);
                var writer = new FrameWriter(stream, limits);
                Assert.Equal(
                    ProtocolFixtures.Bytes("connect-new"),
                    await reader.ReadAsync(token).ConfigureAwait(true)
                );
                await writer
                    .WriteAsync(ProtocolFixtures.Bytes("connect-reply"), token)
                    .ConfigureAwait(true);
                Assert.Equal(
                    ProtocolFixtures.Bytes("get-data-request"),
                    await reader.ReadAsync(token).ConfigureAwait(true)
                );
                await writer
                    .WriteAsync(ProtocolFixtures.Bytes("get-data-reply"), token)
                    .ConfigureAwait(true);
            },
            TestContext.Current.CancellationToken
        );
        await using var lifetime = server.ConfigureAwait(true);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken
        );
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, server.Port, deadline.Token);
        var reader = new FrameReader(client.GetStream(), limits);
        var writer = new FrameWriter(client.GetStream(), limits);
        var connectStorage = new byte[45];
        var connectLength = HandshakeCodec.EncodeNew(connectStorage, 30000, limits);
        await writer.WriteAsync(connectStorage.AsMemory(0, connectLength), deadline.Token);
        using var connected = HandshakeCodec.Decode(
            Assert.IsType<byte[]>(await reader.ReadAsync(deadline.Token)),
            limits
        );
        Assert.True(connected.Accepted);
        var requestStorage = new byte[RequestCodec.Measure(1, ProtocolFixtures.Reads[0], limits)];
        RequestCodec.Encode(requestStorage, 1, ProtocolFixtures.Reads[0], limits);
        await writer.WriteAsync(requestStorage, deadline.Token);
        Assert.IsType<DataResult>(
            ReplyCodec
                .Decode(
                    Assert.IsType<byte[]>(await reader.ReadAsync(deadline.Token)),
                    1,
                    ProtocolFixtures.Reads[0],
                    limits
                )
                .Result
        );
        await server.Completion.ConfigureAwait(true);
    }
}
