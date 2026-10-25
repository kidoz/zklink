using ZkLink.Protocol;

namespace ZkLink.Protocol.Tests;

public sealed class ProtocolGoldenTests
{
    [Fact]
    public void ApacheGeneratedManifestMatchesTheImplementedFixtureCatalog()
    {
        using var document = System.Text.Json.JsonDocument.Parse(
            File.ReadAllText(
                Path.Combine(AppContext.BaseDirectory, "Fixtures", "Protocol", "manifest.json")
            )
        );
        Assert.Equal(
            "a355171b081b5b60749db8f19cca1528b0df936f",
            document.RootElement.GetProperty("sourceCommit").GetString()
        );
        var entries = document.RootElement.GetProperty("cases").EnumerateArray().ToArray();
        Assert.Equal(ProtocolFixtures.All.Length, entries.Length);
        foreach (var entry in entries)
        {
            var fixture = ProtocolFixtures.All.Single(item =>
                item.Name == entry.GetProperty("name").GetString()
            );
            Assert.Equal(fixture.Xid, entry.GetProperty("xid").GetInt32());
            Assert.Equal(fixture.Request.Opcode, entry.GetProperty("opcode").GetInt32());
        }
    }

    public static IEnumerable<object[]> Names =>
        ProtocolFixtures.All.Select(fixture => new object[] { fixture.Name });

    [Theory]
    [MemberData(nameof(Names))]
    [Trait("Acceptance", "T04")]
    public void EveryRequestMatchesApacheJavaAndEveryReplyDecodes(string name)
    {
        var fixture = ProtocolFixtures.All.Single(item => item.Name == name);
        var size = RequestCodec.Measure(fixture.Xid, fixture.Request, ProtocolFixtures.Limits);
        var encoded = new byte[size];
        Assert.Equal(
            size,
            RequestCodec.Encode(encoded, fixture.Xid, fixture.Request, ProtocolFixtures.Limits)
        );
        Assert.Equal(ProtocolFixtures.Bytes(name + "-request"), encoded);
        ProtocolFixtures.Emit(name + "-request", encoded);
        var reply = ReplyCodec.Decode(
            ProtocolFixtures.Bytes(name + "-reply"),
            fixture.Xid,
            fixture.Request,
            ProtocolFixtures.Limits
        );
        Assert.Equal(new ReplyHeader(fixture.Xid, 101, 0), reply.Header);
        ProtocolFixtures.AssertResult(fixture.Result, reply.Result);
        var error = ReplyCodec.Decode(
            ProtocolFixtures.Bytes(name + "-error"),
            fixture.Xid,
            fixture.Request,
            ProtocolFixtures.Limits
        );
        Assert.Equal(new ErrorResult(-101), error.Result);
        Assert.Equal(-101, error.Header.Error);
    }

    [Fact]
    [Trait("Acceptance", "T07")]
    public void NewAndResumeHandshakeMatchApacheJava()
    {
        var storage = new byte[128];
        var length = HandshakeCodec.EncodeNew(storage, 30000, ProtocolFixtures.Limits);
        Assert.Equal(ProtocolFixtures.Bytes("connect-new"), storage[..length]);
        ProtocolFixtures.Emit("connect-new", storage.AsSpan(0, length));
        length = HandshakeCodec.Encode(
            storage,
            30000,
            123,
            12,
            new byte[16],
            ProtocolFixtures.Limits
        );
        Assert.Equal(ProtocolFixtures.Bytes("connect-resume"), storage[..length]);
        ProtocolFixtures.Emit("connect-resume", storage.AsSpan(0, length));
        using var response = HandshakeCodec.Decode(
            ProtocolFixtures.Bytes("connect-reply"),
            ProtocolFixtures.Limits
        );
        Assert.True(response.Accepted);
        Assert.Equal(20000, response.TimeoutMilliseconds);
        Assert.Equal(123, response.SessionId);
        Assert.Equal(new byte[16], response.Password.ToArray());
        using var expired = HandshakeCodec.Decode(
            ProtocolFixtures.Bytes("connect-expired"),
            ProtocolFixtures.Limits
        );
        Assert.False(expired.Accepted);
    }

    [Fact]
    [Trait("Acceptance", "T04")]
    public void NotificationPreservesTypeStatePathAndSeparateZxid()
    {
        var notification = ReplyCodec.DecodeNotification(
            ProtocolFixtures.Bytes("notification"),
            ProtocolFixtures.Limits
        );
        Assert.True(notification.IsRecognized);
        Assert.Equal(3, notification.Type);
        Assert.Equal(3, notification.State);
        Assert.Equal("/node", notification.Path);
        Assert.Equal(102, notification.Zxid);
    }
}
