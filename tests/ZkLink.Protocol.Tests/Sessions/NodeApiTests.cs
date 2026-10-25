using ZkLink.Protocol;

namespace ZkLink.Protocol.Tests.Sessions;

public sealed class NodeApiTests : IAsyncLifetime
{
    private readonly SessionFixture _fixture = new();
    private readonly SessionPeer _peer = new() { ReplyApplications = false };
    private static readonly WireStat Stat = new(11, 22, 33, 44, -2, 6, 7, 88, 9, 10, 111);
    private static readonly ZNodeStat PublicStat = new(11, 22, 33, 44, -2, 6, 7, 88, 9, 10, 111);
    private ZooKeeperClient Client => _fixture.Client!;
    private static readonly string[] ChildNames = ["child", "é"];

    public async ValueTask InitializeAsync()
    {
        await _fixture
            .ConnectAsync(
                _peer,
                SessionFixture.Options(inFlight: 1, connection: "first:2181/app"),
                TestContext.Current.CancellationToken
            )
            .ConfigureAwait(true);
        await SessionTestSupport.NextAsync(_peer).ConfigureAwait(true);
    }

    public async ValueTask DisposeAsync()
    {
        await _fixture.DisposeAsync().ConfigureAwait(true);
        _peer.Dispose();
    }

    [Fact(Timeout = 10000)]
    [Trait("Acceptance", "T15")]
    public async Task ReadsMapEveryStatFieldAndPreserveNullEmptyDataAndChildNames()
    {
        var exists = Client.ExistsAsync("/node", TestContext.Current.CancellationToken);
        var packet = await NextPathAsync(3, "/app/node").ConfigureAwait(true);
        Reply(packet, new StatResult(Stat));
        Assert.Equal(PublicStat, await exists.ConfigureAwait(true));
        foreach (byte[]? data in new byte[]?[] { null, [], [1, 2, 3] })
        {
            var read = Client.GetDataAsync("/node", TestContext.Current.CancellationToken);
            packet = await NextPathAsync(4, "/app/node").ConfigureAwait(true);
            Reply(packet, new DataResult(data, Stat));
            ZNodeData result = await read.ConfigureAwait(true);
            Assert.Equal(PublicStat, result.Stat);
            Assert.Equal(data is null, !result.Data.HasValue);
            if (data is not null)
                Assert.Equal(data, result.Data!.Value.ToArray());
        }
        var children = Client.GetChildrenAsync("/", TestContext.Current.CancellationToken);
        packet = await NextPathAsync(12, "/app").ConfigureAwait(true);
        Reply(packet, new StringsResult(ChildNames, Stat));
        ZNodeChildren names = await children.ConfigureAwait(true);
        Assert.Equal(ChildNames, names.Children);
        Assert.Equal(PublicStat, names.Stat);
    }

    [Fact(Timeout = 10000)]
    public async Task OnlyExistsConvertsNoNodeToNullAndOtherErrorsPreserveMetadata()
    {
        var exists = Client.ExistsAsync("/missing", TestContext.Current.CancellationToken);
        _peer.Reply(await SessionTestSupport.NextAsync(_peer).ConfigureAwait(true), -101);
        Assert.Null(await exists.ConfigureAwait(true));
        var read = Client.GetDataAsync("/missing", TestContext.Current.CancellationToken);
        var packet = await SessionTestSupport.NextAsync(_peer).ConfigureAwait(true);
        _peer.Reply(packet, -101);
        ZooKeeperException error = await Assert
            .ThrowsAsync<ZooKeeperException>(() => read)
            .ConfigureAwait(true);
        Assert.Equal(RequestOutcome.Rejected, error.Outcome);
        Assert.Equal(-101, error.ErrorCode);
        Assert.Equal("/missing", error.Request.Path);
        Assert.Equal(ZooKeeperOperation.GetData, error.Request.Operation);
        Assert.Equal(packet.Xid, error.Request.RequestId);
        exists = Client.ExistsAsync("/denied", TestContext.Current.CancellationToken);
        _peer.Reply(await SessionTestSupport.NextAsync(_peer).ConfigureAwait(true), -102);
        await Assert.ThrowsAsync<ZooKeeperException>(() => exists).ConfigureAwait(true);
        Assert.Equal(ZooKeeperSessionState.Connected, Client.Session.State);
    }

    [Theory(Timeout = 10000)]
    [InlineData(CreateMode.Persistent, 15)]
    [InlineData(CreateMode.Ephemeral, 15)]
    [InlineData(CreateMode.PersistentSequential, 15)]
    [InlineData(CreateMode.EphemeralSequential, 15)]
    [InlineData(CreateMode.Container, 19)]
    [Trait("Acceptance", "T16")]
    public async Task CreateUsesOneStatReturningRequestWithExplicitAcl(CreateMode mode, int opcode)
    {
        var create = Client.CreateAsync(
            "/new",
            null,
            new CreateOptions([ZooKeeperAcl.OpenUnsafe], mode),
            TestContext.Current.CancellationToken
        );
        var packet = await NextPathAsync(opcode, "/app/new").ConfigureAwait(true);
        var input = new JuteReader(packet.Bytes, ProtocolFixtures.Limits);
        input.ReadRequestHeader();
        input.ReadString();
        Assert.Null(input.ReadBuffer());
        Assert.Equal(1, input.ReadInt32());
        Assert.Equal(31, input.ReadInt32());
        Assert.Equal("world", input.ReadString());
        Assert.Equal("anyone", input.ReadString());
        Assert.Equal((int)mode, input.ReadInt32());
        input.EnsureEnd();
        string returnedPath = mode
            is CreateMode.PersistentSequential
                or CreateMode.EphemeralSequential
            ? "/app/new-0000000042"
            : "/app/new";
        Reply(packet, new PathResult(returnedPath, Stat));
        CreateResult result = await create.ConfigureAwait(true);
        Assert.Equal(returnedPath[4..], result.Path);
        Assert.Equal(PublicStat, result.Stat);
        Assert.Equal(0, _peer.Requests.Count);
    }

    [Fact(Timeout = 10000)]
    [Trait("Acceptance", "T15")]
    public async Task QueuedSetDataAndAclCaptureInputsAndUseIndependentVersionConditions()
    {
        var blocker = Client.ExistsAsync("/block", TestContext.Current.CancellationToken);
        var blockedPacket = await SessionTestSupport.NextAsync(_peer).ConfigureAwait(true);
        byte[] data = [1, 2, 3];
        var set = Client.SetDataAsync(
            "/data",
            data,
            ZNodeVersion.Exact(int.MinValue),
            TestContext.Current.CancellationToken
        );
        data[0] = 99;
        var acl = new List<ZooKeeperAcl> { new(ZooKeeperPermissions.Read, "world", "anyone") };
        var setAcl = Client.SetAclAsync(
            "/data",
            acl,
            ZNodeVersion.Exact(7),
            TestContext.Current.CancellationToken
        );
        acl.Clear();
        Reply(blockedPacket, new StatResult(Stat));
        await blocker.ConfigureAwait(true);
        var packet = await NextPathAsync(5, "/app/data").ConfigureAwait(true);
        var input = new JuteReader(packet.Bytes, ProtocolFixtures.Limits);
        input.ReadRequestHeader();
        input.ReadString();
        Assert.Equal(new byte[] { 1, 2, 3 }, input.ReadBuffer());
        Assert.Equal(int.MinValue, input.ReadInt32());
        Reply(packet, new StatResult(Stat));
        Assert.Equal(PublicStat, await set.ConfigureAwait(true));
        packet = await NextPathAsync(7, "/app/data").ConfigureAwait(true);
        input = new JuteReader(packet.Bytes, ProtocolFixtures.Limits);
        input.ReadRequestHeader();
        input.ReadString();
        Assert.Equal(1, input.ReadInt32());
        Assert.Equal(1, input.ReadInt32());
        Assert.Equal("world", input.ReadString());
        Assert.Equal("anyone", input.ReadString());
        Assert.Equal(7, input.ReadInt32());
        input.EnsureEnd();
        Reply(packet, new StatResult(Stat));
        Assert.Equal(PublicStat, await setAcl.ConfigureAwait(true));
    }

    [Fact(Timeout = 10000)]
    public async Task GetAclPreservesSchemesAndDeleteUsesDataVersion()
    {
        var get = Client.GetAclAsync("/node", TestContext.Current.CancellationToken);
        var packet = await NextPathAsync(6, "/app/node").ConfigureAwait(true);
        Reply(
            packet,
            new AclResult(
                [new WireAcl(1, "digest", "identity"), new WireAcl(16, "custom", "opaque")],
                Stat
            )
        );
        ZNodeAcl result = await get.ConfigureAwait(true);
        Assert.Equal(PublicStat, result.Stat);
        Assert.Equal("digest", result.Acl[0].Scheme);
        Assert.Equal(ZooKeeperPermissions.Read, result.Acl[0].Permissions);
        Assert.Equal("custom", result.Acl[1].Scheme);
        Assert.Equal("opaque", result.Acl[1].Id);
        var delete = Client.DeleteAsync(
            "/node",
            ZNodeVersion.Any,
            TestContext.Current.CancellationToken
        );
        packet = await NextPathAsync(2, "/app/node").ConfigureAwait(true);
        var input = new JuteReader(packet.Bytes, ProtocolFixtures.Limits);
        input.ReadRequestHeader();
        input.ReadString();
        Assert.Equal(-1, input.ReadInt32());
        input.EnsureEnd();
        Reply(packet, new EmptyResult());
        await delete.ConfigureAwait(true);
    }

    [Fact(Timeout = 10000)]
    public async Task SentCreateCancellationRetainsLateReplyAndDoesNotReplay()
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken
        );
        var create = Client.CreateAsync(
            "/seq-",
            null,
            new CreateOptions([ZooKeeperAcl.OpenUnsafe], CreateMode.PersistentSequential),
            stop.Token
        );
        var packet = await SessionTestSupport.NextAsync(_peer).ConfigureAwait(true);
        await stop.CancelAsync().ConfigureAwait(true);
        ZooKeeperOperationCanceledException error = await Assert
            .ThrowsAsync<ZooKeeperOperationCanceledException>(() => create)
            .ConfigureAwait(true);
        Assert.Equal(RequestOutcome.Unknown, error.Outcome);
        Assert.Equal(ZooKeeperOperation.CreateWithStat, error.Request.Operation);
        var next = Client.ExistsAsync("/later", TestContext.Current.CancellationToken);
        Reply(packet, new PathResult("/app/seq-0000000001", Stat));
        packet = await SessionTestSupport.NextAsync(_peer).ConfigureAwait(true);
        Assert.Equal(3, packet.Opcode);
        Reply(packet, new StatResult(Stat));
        await next.ConfigureAwait(true);
        Assert.Equal(0, _peer.Requests.Count);
        Assert.Equal(0, Client.Manager.Scheduler.OwnedBytes);
    }

    [Fact]
    public void InvalidInputsAndUnavailableTtlFailBeforeSending()
    {
        Assert.Throws<ArgumentException>(() =>
        {
            _ = Client.GetDataAsync("relative", TestContext.Current.CancellationToken);
        });
        Assert.Throws<ArgumentException>(() =>
        {
            _ = Client.SetAclAsync(
                "/node",
                [],
                ZNodeVersion.Any,
                TestContext.Current.CancellationToken
            );
        });
        Assert.Throws<ArgumentException>(() =>
        {
            _ = Client.SetAclAsync(
                "/node",
                [null!],
                ZNodeVersion.Any,
                TestContext.Current.CancellationToken
            );
        });
        Assert.Throws<NotSupportedException>(() =>
        {
            _ = Client.CreateAsync(
                "/ttl",
                null,
                new CreateOptions(
                    [ZooKeeperAcl.OpenUnsafe],
                    CreateMode.PersistentWithTtl,
                    TimeSpan.FromSeconds(1)
                ),
                TestContext.Current.CancellationToken
            );
        });
        Assert.Equal(0, _peer.Requests.Count);
    }

    [Fact(Timeout = 10000)]
    [Trait("Acceptance", "T15")]
    public async Task OversizedDataAndFramesAreRejectedLocallyAndLeaveTheSessionUsable()
    {
        byte[] oversized = new byte[1_000_001];
        Assert.Throws<InvalidDataException>(() =>
        {
            _ = Client.SetDataAsync(
                "/node",
                oversized,
                ZNodeVersion.Any,
                TestContext.Current.CancellationToken
            );
        });
        string longPath = "/" + new string('a', ProtocolLimits.DefaultMaxFrameBytes - 8);
        Assert.Throws<InvalidDataException>(() =>
        {
            _ = Client.CreateAsync(
                longPath,
                null,
                new CreateOptions([ZooKeeperAcl.OpenUnsafe]),
                TestContext.Current.CancellationToken
            );
        });
        Assert.Equal(0, _peer.Requests.Count);
        var exists = Client.ExistsAsync("/valid", TestContext.Current.CancellationToken);
        var packet = await SessionTestSupport.NextAsync(_peer).ConfigureAwait(true);
        Reply(packet, new StatResult(Stat));
        Assert.Equal(PublicStat, await exists.ConfigureAwait(true));
    }

    private async Task<ReceivedPacket> NextPathAsync(int opcode, string path)
    {
        var packet = await SessionTestSupport.NextAsync(_peer).ConfigureAwait(false);
        Assert.Equal(opcode, packet.Opcode);
        var input = new JuteReader(packet.Bytes, ProtocolFixtures.Limits);
        input.ReadRequestHeader();
        Assert.Equal(path, input.ReadString());
        if (opcode is 3 or 4 or 12)
        {
            Assert.False(input.ReadBoolean());
            input.EnsureEnd();
        }
        return packet;
    }

    private void Reply(ReceivedPacket packet, WireResult result)
    {
        var bytes = new byte[4096];
        var writer = new JuteWriter(bytes, ProtocolFixtures.Limits);
        writer.WriteReplyHeader(new ReplyHeader(packet.Xid, 12, 0));
        switch (result)
        {
            case StatResult stat:
                writer.WriteStat(stat.Stat);
                break;
            case DataResult data:
                if (data.Data is null)
                    writer.WriteNullBuffer();
                else
                    writer.WriteBuffer(data.Data);
                writer.WriteStat(data.Stat);
                break;
            case StringsResult strings:
                writer.WriteStringVector(strings.Values);
                writer.WriteStat(strings.Stat!.Value);
                break;
            case PathResult path:
                writer.WriteString(path.Path);
                writer.WriteStat(path.Stat!.Value);
                break;
            case AclResult acl:
                writer.WriteInt32(acl.Acl.Length);
                foreach (var entry in acl.Acl)
                {
                    writer.WriteInt32(entry.Permissions);
                    writer.WriteString(entry.Scheme);
                    writer.WriteString(entry.Id);
                }
                writer.WriteStat(acl.Stat);
                break;
            case EmptyResult:
                break;
            default:
                throw new InvalidOperationException("Unknown test reply.");
        }
        _peer.Inject(bytes[..writer.WrittenCount]);
    }
}
