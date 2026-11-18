using System.Buffers.Binary;
using System.Globalization;
using ZkLink.Configuration;
using ZkLink.Protocol;

namespace ZkLink.Protocol.Tests;

// Bounded, schema-valid cases. The committed Apache fixtures supply independent reply bodies.
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Security",
    "CA5394",
    Justification = "The decoder corpus uses deterministic random values so a reported seed reproduces its test cases."
)]
public sealed class DecoderPropertyTests
{
    private static string? CampaignSeed =>
        Environment.GetEnvironmentVariable("ZKLINK_DECODER_CAMPAIGN_SEED");

    public static TheoryData<int> Seeds
    {
        get
        {
            if (CampaignSeed is { Length: > 0 } candidate)
            {
                return new TheoryData<int> { int.Parse(candidate, CultureInfo.InvariantCulture) };
            }
            var seeds = new TheoryData<int> { 17, 1729, 65537 };
            if (Environment.GetEnvironmentVariable("ZKLINK_DECODER_SEED") is { Length: > 0 } seed)
                seeds.Add(int.Parse(seed, CultureInfo.InvariantCulture));
            return seeds;
        }
    }

    [Theory]
    [MemberData(nameof(Seeds))]
    public void TruncatedHandshakeAndTrailingNotificationDataAreRejected(int seed)
    {
        var handshake = ProtocolFixtures.Bytes("connect-reply");
        int length = (int)((uint)seed % (handshake.Length - 1));
        Assert.Throws<InvalidDataException>(() =>
        {
            using var unexpected = HandshakeCodec.Decode(
                handshake.AsSpan(0, length),
                ProtocolFixtures.Limits
            );
        });

        var notification = new byte[128];
        var writer = new JuteWriter(notification, ProtocolFixtures.Limits);
        writer.WriteReplyHeader(new ReplyHeader(-1, seed, 0));
        writer.WriteInt32(3);
        writer.WriteInt32(3);
        writer.WriteString("/fixture");
        int trailing = 1 + (int)((uint)seed % 7);
        int size = writer.WrittenCount + trailing;
        Assert.Throws<InvalidDataException>(() =>
            ReplyCodec.DecodeNotification(notification.AsSpan(0, size), ProtocolFixtures.Limits)
        );
    }

    [Theory]
    [MemberData(nameof(Seeds))]
    [Trait("Acceptance", "T04")]
    public async Task FramedRepliesPreserveFixtureResultsAndGeneratedZxids(int seed)
    {
        var random = new Random(seed);
        var fixtures = ProtocolFixtures.All;
        using var stream = new MemoryStream();
        var expected = new List<(ProtocolFixture Fixture, long Zxid)>();
        var cases = CampaignSeed is null
            ? random.GetItems(fixtures, 64)
            : [fixtures[(int)((uint)seed % fixtures.Length)]];
        foreach (var fixture in cases)
        {
            var payload = ProtocolFixtures.Bytes(fixture.Name + "-reply");
            long zxid = random.NextInt64();
            BinaryPrimitives.WriteInt64BigEndian(payload.AsSpan(sizeof(int)), zxid);
            var prefix = new byte[sizeof(int)];
            BinaryPrimitives.WriteInt32BigEndian(prefix, payload.Length);
            stream.Write(prefix);
            stream.Write(payload);
            expected.Add((fixture, zxid));
        }

        stream.Position = 0;
        var reader = new FrameReader(stream, ProtocolFixtures.Limits);
        foreach (var (fixture, zxid) in expected)
        {
            var frame = await reader.ReadAsync(TestContext.Current.CancellationToken);
            Assert.NotNull(frame);
            var reply = ReplyCodec.Decode(
                frame,
                fixture.Xid,
                fixture.Request,
                ProtocolFixtures.Limits
            );
            Assert.Equal(new ReplyHeader(fixture.Xid, zxid, 0), reply.Header);
            ProtocolFixtures.AssertResult(fixture.Result, reply.Result);
        }
        Assert.Null(await reader.ReadAsync(TestContext.Current.CancellationToken));
    }

    [Theory]
    [MemberData(nameof(Seeds))]
    [Trait("Acceptance", "T05")]
    public void NotificationsAndConnectionStringsPreserveGeneratedPaths(int seed)
    {
        var random = new Random(seed);
        var bytes = new byte[256];
        for (var i = 0; i < 64; i++)
        {
            var path = "/" + new string(random.GetItems("abcЖé".ToCharArray(), random.Next(1, 40)));
            var zxid = random.NextInt64();
            var type = random.Next(1, 5);
            var writer = new JuteWriter(bytes, ProtocolFixtures.Limits);
            writer.WriteReplyHeader(new ReplyHeader(-1, zxid, 0));
            writer.WriteInt32(type);
            writer.WriteInt32(3);
            writer.WriteString(path);
            var notification = ReplyCodec.DecodeNotification(
                bytes.AsSpan(0, writer.WrittenCount),
                ProtocolFixtures.Limits
            );
            Assert.Equal(new WireNotification(type, 3, path, zxid), notification);

            var port = random.Next(1, 65536);
            bool ipv6 = (seed & 1) != 0;
            string authority = ipv6 ? "[::1]" : "zk.example";
            bool multiple = (seed & 2) != 0;
            var parsed = ConnectionStringParser.Parse(
                FormattableString.Invariant(
                    $"{authority}:{port}{(multiple ? ",replica" : "")}{path}"
                )
            );
            Assert.Equal(path, parsed.Chroot);
            Assert.Equal(multiple ? 2 : 1, parsed.Endpoints.Count);
            var endpoint = parsed.Endpoints[0];
            Assert.Equal(ipv6 ? "::1" : "zk.example", endpoint.Host);
            Assert.Equal(port, endpoint.Port);
        }
    }

    [Theory]
    [MemberData(nameof(Seeds))]
    [Trait("Acceptance", "T07")]
    public void HandshakePreservesGeneratedAcceptedSessionFields(int seed)
    {
        var random = new Random(seed);
        var bytes = ProtocolFixtures.Bytes("connect-reply");
        for (var i = 0; i < 64; i++)
        {
            var timeout = random.Next(2000, 60001);
            var sessionId = random.NextInt64(1, long.MaxValue);
            BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(sizeof(int)), timeout);
            BinaryPrimitives.WriteInt64BigEndian(bytes.AsSpan(2 * sizeof(int)), sessionId);
            bool legacy = (seed & 4) != 0;
            using var reply = HandshakeCodec.Decode(
                legacy ? bytes.AsSpan(0, bytes.Length - 1) : bytes,
                ProtocolFixtures.Limits
            );
            Assert.True(reply.Accepted);
            Assert.Equal(timeout, reply.TimeoutMilliseconds);
            Assert.Equal(sessionId, reply.SessionId);
        }
    }
}
