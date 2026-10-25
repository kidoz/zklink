using System.Buffers.Binary;
using ZkLink.Configuration;
using ZkLink.Protocol;

namespace ZkLink.Protocol.Tests;

public sealed class ProtocolValidationTests
{
    public static IEnumerable<object[]> Names => ProtocolGoldenTests.Names;

    [Theory]
    [MemberData(nameof(Names))]
    [Trait("Acceptance", "T03")]
    public void AllTruncationsAndUnexpectedTailsAreRejected(string name)
    {
        var fixture = ProtocolFixtures.All.Single(item => item.Name == name);
        var bytes = ProtocolFixtures.Bytes(name + "-reply");
        for (var length = 0; length < bytes.Length; length++)
        {
            var truncated = bytes[..length];
            Assert.Throws<InvalidDataException>(() =>
                ReplyCodec.Decode(truncated, fixture.Xid, fixture.Request, ProtocolFixtures.Limits)
            );
        }
        Assert.Throws<InvalidDataException>(() =>
            ReplyCodec.Decode([.. bytes, 0], fixture.Xid, fixture.Request, ProtocolFixtures.Limits)
        );
        var error = ProtocolFixtures.Bytes(name + "-error");
        Assert.Throws<InvalidDataException>(() =>
            ReplyCodec.Decode([.. error, 0], fixture.Xid, fixture.Request, ProtocolFixtures.Limits)
        );
    }

    [Fact]
    [Trait("Acceptance", "T04")]
    public void ErrorCodesRemainRawAndRepliesMustMatchTheExpectedXid()
    {
        var request = new ReadRequest(4, "/node", false);
        var error = ProtocolFixtures.Bytes("get-data-error");
        BinaryPrimitives.WriteInt32BigEndian(error.AsSpan(12), -999);
        var reply = ReplyCodec.Decode(error, 1, request, ProtocolFixtures.Limits);
        Assert.Equal(-999, reply.Header.Error);
        Assert.Equal(new ErrorResult(-999), reply.Result);
        Assert.Throws<InvalidDataException>(() =>
            ReplyCodec.Decode(error, 2, request, ProtocolFixtures.Limits)
        );
        Assert.Throws<InvalidDataException>(() =>
            ReplyCodec.Decode(error, 1, new EmptyRequest(999), ProtocolFixtures.Limits)
        );
        foreach (var control in ProtocolFixtures.All.Where(f => f.Xid < 0))
        {
            Assert.Throws<InvalidDataException>(() =>
                ReplyCodec.Decode(
                    ProtocolFixtures.Bytes(control.Name + "-reply"),
                    1,
                    control.Request,
                    ProtocolFixtures.Limits
                )
            );
        }
        Assert.Throws<InvalidDataException>(() =>
            ReplyCodec.Decode(
                ProtocolFixtures.Bytes("notification"),
                1,
                request,
                ProtocolFixtures.Limits
            )
        );
    }

    [Fact]
    public void UnknownNotificationValuesRemainExplicitAndUnavailableZxidStaysNull()
    {
        var bytes = ProtocolFixtures.Bytes("notification");
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(16), 999);
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(20), 888);
        BinaryPrimitives.WriteInt64BigEndian(bytes.AsSpan(4), -1);
        var result = ReplyCodec.DecodeNotification(bytes, ProtocolFixtures.Limits);
        Assert.False(result.IsRecognized);
        Assert.Equal(999, result.Type);
        Assert.Equal(888, result.State);
        Assert.Null(result.Zxid);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(int.MaxValue)]
    [InlineData(100_001)]
    public void InvalidResponseVectorsFailBeforeAllocation(int count)
    {
        var bytes = ProtocolFixtures.Bytes("get-children-reply");
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(16), count);
        Assert.Throws<InvalidDataException>(() =>
            ReplyCodec.Decode(bytes, 1, new ReadRequest(8, "/node", false), ProtocolFixtures.Limits)
        );
    }

    [Fact]
    [Trait("Acceptance", "T04")]
    public void MultiTerminatorsTypesCountsAndErrorBodiesAreChecked()
    {
        var request = new MultiRequest(false, ProtocolFixtures.Writes);
        var original = ProtocolFixtures.Bytes("multi-write-reply");
        foreach (
            var offset in new[] { original.Length - 9, original.Length - 5, original.Length - 1 }
        )
        {
            var bytes = (byte[])original.Clone();
            bytes[offset] ^= 1;
            Assert.Throws<InvalidDataException>(() =>
                ReplyCodec.Decode(bytes, 1, request, ProtocolFixtures.Limits)
            );
        }
        var wrongType = (byte[])original.Clone();
        BinaryPrimitives.WriteInt32BigEndian(wrongType.AsSpan(16), 999);
        Assert.Throws<InvalidDataException>(() =>
            ReplyCodec.Decode(wrongType, 1, request, ProtocolFixtures.Limits)
        );
        var earlyDone = (byte[])original.Clone();
        earlyDone[20] = 1;
        Assert.Throws<InvalidDataException>(() =>
            ReplyCodec.Decode(earlyDone, 1, request, ProtocolFixtures.Limits)
        );
        var wrongError = ProtocolFixtures.Bytes("multi-failed-reply");
        BinaryPrimitives.WriteInt32BigEndian(wrongError.AsSpan(25), -999);
        Assert.Throws<InvalidDataException>(() =>
            ReplyCodec.Decode(wrongError, 1, request, ProtocolFixtures.Limits)
        );
        Assert.Throws<InvalidDataException>(() =>
            ReplyCodec.Decode(
                original,
                1,
                new MultiRequest(false, ProtocolFixtures.Writes[..1]),
                ProtocolFixtures.Limits
            )
        );
        var ttlType = ProtocolFixtures.Bytes("multi-write-reply");
        // TTL/container success headers must normalize to create2, not their request opcode.
        var containerOffset = 16 + (9 + 9) + (9 + 9 + 68);
        BinaryPrimitives.WriteInt32BigEndian(ttlType.AsSpan(containerOffset), 19);
        Assert.Throws<InvalidDataException>(() =>
            ReplyCodec.Decode(ttlType, 1, request, ProtocolFixtures.Limits)
        );
    }

    [Fact]
    [Trait("Acceptance", "T04")]
    public void InvalidMixedNestedAndWatchedMultiRequestsDoNotTouchDestination()
    {
        WireRequest[] invalid =
        [
            new MultiRequest(false, [ProtocolFixtures.Writes[0], ProtocolFixtures.Reads[0]]),
            new MultiRequest(true, [ProtocolFixtures.Reads[0], ProtocolFixtures.Writes[0]]),
            new MultiRequest(true, [new ReadRequest(4, "/node", true)]),
            new MultiRequest(false, [new MultiRequest(false, [])]),
            new MultiRequest(true, [new ReadRequest(12, "/node", false)]),
            new CreateRequest(19, "/node", null, ProtocolFixtures.Acl, 0),
            new CreateRequest(21, "/node", null, ProtocolFixtures.Acl, 5, 0),
            new EmptyRequest(20),
            new EmptyRequest(16),
            new EmptyRequest(102),
            new EmptyRequest(999),
        ];
        var storage = new byte[1024];
        Array.Fill(storage, (byte)0x5a);
        foreach (var request in invalid)
        {
            Assert.Throws<InvalidDataException>(() =>
                RequestCodec.Encode(storage, 1, request, ProtocolFixtures.Limits)
            );
        }
        Assert.All(storage, value => Assert.Equal(0x5a, value));
    }

    [Fact]
    public void FrameDataCollectionAndChrootExpansionBudgetsAreIndependent()
    {
        var set = new SetDataRequest("/node", ProtocolFixtures.Data, -1);
        Assert.Throws<InvalidDataException>(() =>
            RequestCodec.Measure(1, set, new ProtocolLimits(maxDataBytes: 2))
        );
        Assert.Throws<InvalidDataException>(() =>
            ReplyCodec.Decode(
                ProtocolFixtures.Bytes("get-data-reply"),
                1,
                ProtocolFixtures.Reads[0],
                new ProtocolLimits(maxDataBytes: 2)
            )
        );
        Assert.Throws<InvalidDataException>(() =>
            RequestCodec.Measure(1, set, new ProtocolLimits(maxFrameBytes: 8))
        );
        Assert.Throws<InvalidDataException>(() =>
            RequestCodec.Measure(
                -8,
                new RestoreRequest(12, ["/a", "/b"], [], []),
                new ProtocolLimits(maxCollectionElements: 1)
            )
        );
        var paths = new ChrootPath("/long-namespace");
        Assert.Throws<InvalidDataException>(() =>
            NamespaceTranslator.ToServer(
                new ReadRequest(4, "/node", false),
                paths,
                new ProtocolLimits(maxFrameBytes: 20)
            )
        );
    }

    [Fact]
    [Trait("Acceptance", "T07")]
    public void HandshakeAcceptsOnlyTheExactLegacyBoundaryAndClearsOwnedSecrets()
    {
        var bytes = ProtocolFixtures.Bytes("connect-reply");
        using var legacy = HandshakeCodec.Decode(
            bytes.AsSpan(0, bytes.Length - 1),
            ProtocolFixtures.Limits
        );
        Assert.True(legacy.Accepted);
        for (var length = 0; length < bytes.Length - 1; length++)
        {
            var truncated = bytes[..length];
            Assert.Throws<InvalidDataException>(() =>
                HandshakeCodec.Decode(truncated, ProtocolFixtures.Limits)
            );
        }
        var readOnly = (byte[])bytes.Clone();
        readOnly[^1] = 1;
        Assert.Throws<InvalidDataException>(() =>
            HandshakeCodec.Decode(readOnly, ProtocolFixtures.Limits)
        );
        var version = (byte[])bytes.Clone();
        version[3] = 1;
        Assert.Throws<InvalidDataException>(() =>
            HandshakeCodec.Decode(version, ProtocolFixtures.Limits)
        );
        var oversized = (byte[])bytes.Clone();
        BinaryPrimitives.WriteInt32BigEndian(oversized.AsSpan(16), 17);
        Assert.Throws<InvalidDataException>(() =>
            HandshakeCodec.Decode(oversized, ProtocolFixtures.Limits)
        );
        Assert.Throws<InvalidDataException>(() =>
            HandshakeCodec.Decode([.. bytes, 0], ProtocolFixtures.Limits)
        );
        bytes.AsSpan(20, 16).Fill(0x5a);
        var response = HandshakeCodec.Decode(bytes, ProtocolFixtures.Limits);
        var secret = response.Password;
        response.Dispose();
        response.Dispose();
        Assert.All(secret.ToArray(), value => Assert.Equal(0, value));
        Assert.Equal(0x5a, bytes[20]);
        var destination = new byte[128];
        Assert.Throws<InvalidDataException>(() =>
            HandshakeCodec.Encode(destination, 30000, 0, 1, new byte[16], ProtocolFixtures.Limits)
        );
        var nonzeroPassword = new byte[16];
        nonzeroPassword[0] = 1;
        Assert.Throws<InvalidDataException>(() =>
            HandshakeCodec.Encode(
                destination,
                30000,
                0,
                0,
                nonzeroPassword,
                ProtocolFixtures.Limits
            )
        );
    }

    [Fact]
    public void ChrootBudgetUsesUtf8BytesRatherThanCharacterCount()
    {
        var request = new ReadRequest(4, "/node", false);
        var paths = new ChrootPath("/Ж");
        Assert.Throws<InvalidDataException>(() =>
            NamespaceTranslator.ToServer(request, paths, new ProtocolLimits(maxFrameBytes: 20))
        );
        var limits = new ProtocolLimits(maxFrameBytes: 21);
        var translated = NamespaceTranslator.ToServer(request, paths, limits);
        Assert.Equal(21, RequestCodec.Measure(1, translated, limits));
    }

    [Theory]
    [InlineData(8, "/absolute-name")]
    [InlineData(8, "..")]
    [InlineData(8, "")]
    [InlineData(103, "relative-path")]
    public void ChildNamesAndFullPathsCannotBeConfused(int opcode, string value)
    {
        var storage = new byte[128];
        var writer = new JuteWriter(storage, ProtocolFixtures.Limits);
        writer.WriteReplyHeader(new ReplyHeader(1, 12, 0));
        writer.WriteStringVector([value]);
        var payload = storage[..writer.WrittenCount];
        WireRequest request =
            opcode == 8 ? new ReadRequest(8, "/node", false) : new PathRequest(103, "/node");
        Assert.Throws<InvalidDataException>(() =>
            ReplyCodec.Decode(payload, 1, request, ProtocolFixtures.Limits)
        );
    }
}
