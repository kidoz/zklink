using ZkLink.Protocol;

namespace ZkLink.Protocol.Tests.Sessions;

public sealed class TtlApiTests : IAsyncLifetime
{
    private const int TestTimeoutMilliseconds = 10_000;
    private const int ReplyBufferBytes = 4096;
    private const int SuccessErrorCode = 0;
    private const int MultiSentinel = -1;
    private const long ReplyZxid = 12;
    private const long MinimumTtlMilliseconds = 1;
    private const long TestTtlMilliseconds = 1234;
    private const int OversizedDataBytes = 1_000_001;
    private readonly SessionFixture _fixture = new();
    private readonly SessionPeer _peer = new() { ReplyApplications = false };
    private ZooKeeperClient Client => _fixture.Client!;

    public async ValueTask InitializeAsync()
    {
        await _fixture
            .ConnectAsync(
                _peer,
                SessionFixture.Options(
                    inFlight: 1,
                    connection: "first:2181/app",
                    enableTtlNodes: true
                ),
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

    [Theory(Timeout = TestTimeoutMilliseconds)]
    [InlineData(CreateMode.PersistentWithTtl, MinimumTtlMilliseconds)]
    [InlineData(CreateMode.PersistentWithTtl, CreateOptions.MaxTtlMilliseconds)]
    [InlineData(CreateMode.PersistentSequentialWithTtl, MinimumTtlMilliseconds)]
    [InlineData(CreateMode.PersistentSequentialWithTtl, CreateOptions.MaxTtlMilliseconds)]
    [Trait("Acceptance", "T17")]
    public async Task SingleCreateCapturesDataAndEncodesExactTtlBoundsAndChroot(
        CreateMode mode,
        long milliseconds
    )
    {
        Task blocker = Client.SyncAsync("/block", TestContext.Current.CancellationToken);
        var blocked = await SessionTestSupport.NextAsync(_peer).ConfigureAwait(true);
        byte[] data = [1];
        string path = mode == CreateMode.PersistentSequentialWithTtl ? "/" : "/new";
        var pending = Client.CreateAsync(
            path,
            data,
            Options(mode, milliseconds),
            TestContext.Current.CancellationToken
        );
        data[0] = 99;
        _peer.Reply(blocked);
        await blocker.ConfigureAwait(true);
        var packet = await SessionTestSupport.NextAsync(_peer).ConfigureAwait(true);
        Assert.Equal((int)ZooKeeperOperation.CreateTtl, packet.Opcode);
        var input = new JuteReader(packet.Bytes, ProtocolFixtures.Limits);
        input.ReadRequestHeader();
        AssertCreateBody(ref input, path == "/" ? "/app/" : "/app/new", mode, milliseconds);
        input.EnsureEnd();
        string actual = path == "/" ? "/app/0000000042" : "/app/new";
        InjectCreates(packet, false, actual);
        CreateResult created = await pending.ConfigureAwait(true);
        Assert.Equal(actual[4..], created.Path);
        Assert.Equal(ProtocolFixtures.Stat.Mzxid, created.Stat.Mzxid);
        Assert.Equal(0, _peer.Requests.Count);
    }

    [Fact(Timeout = TestTimeoutMilliseconds)]
    [Trait("Acceptance", "T18")]
    public async Task TransactionCapturesBothTtlModesAndNormalizesCreateResults()
    {
        byte[] data = [1];
        ZooKeeperWriteOperation[] operations =
        [
            ZooKeeperWriteOperation.Create(
                "/first",
                data,
                Options(CreateMode.PersistentWithTtl, TestTtlMilliseconds)
            ),
            ZooKeeperWriteOperation.Create(
                "/seq-",
                data,
                Options(CreateMode.PersistentSequentialWithTtl, TestTtlMilliseconds)
            ),
        ];
        var pending = Client.MultiAsync(operations, TestContext.Current.CancellationToken);
        data[0] = 99;
        var packet = await SessionTestSupport.NextAsync(_peer).ConfigureAwait(true);
        Assert.Equal((int)ZooKeeperOperation.Multi, packet.Opcode);
        var input = new JuteReader(packet.Bytes, ProtocolFixtures.Limits);
        input.ReadRequestHeader();
        foreach (var operation in operations)
        {
            Assert.Equal((int)ZooKeeperOperation.CreateTtl, input.ReadInt32());
            Assert.False(input.ReadBoolean());
            Assert.Equal(MultiSentinel, input.ReadInt32());
            AssertCreateBody(
                ref input,
                "/app" + operation.Path,
                operation.Path == "/first"
                    ? CreateMode.PersistentWithTtl
                    : CreateMode.PersistentSequentialWithTtl,
                TestTtlMilliseconds
            );
        }
        Assert.Equal(MultiSentinel, input.ReadInt32());
        Assert.True(input.ReadBoolean());
        Assert.Equal(MultiSentinel, input.ReadInt32());
        input.EnsureEnd();
        InjectCreates(packet, true, "/app/first", "/app/seq-0000000042");
        var results = await pending.ConfigureAwait(true);
        Assert.Equal("/first", Assert.IsType<ZooKeeperCreatedResult>(results[0]).Value.Path);
        Assert.Equal(
            "/seq-0000000042",
            Assert.IsType<ZooKeeperCreatedResult>(results[1]).Value.Path
        );
    }

    [Theory(Timeout = TestTimeoutMilliseconds)]
    [InlineData(CreateMode.PersistentWithTtl)]
    [InlineData(CreateMode.PersistentSequentialWithTtl)]
    [Trait("Acceptance", "T17")]
    public async Task DefaultClientRejectsSingleAndEntireBatchLocally(CreateMode mode)
    {
        var fixture = new SessionFixture();
        await using var lifetime = fixture.ConfigureAwait(true);
        using var peer = new SessionPeer { ReplyApplications = false };
        var disabled = await fixture
            .ConnectAsync(peer, cancellationToken: TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        await SessionTestSupport.NextAsync(peer).ConfigureAwait(true);
        var options = Options(mode, TestTtlMilliseconds);
        ZooKeeperWriteOperation descriptor = ZooKeeperWriteOperation.Create("/ttl", null, options);
        Assert.Equal(ZooKeeperOperation.CreateTtl, descriptor.Operation);
        Assert.Throws<NotSupportedException>(() =>
        {
            _ = disabled.CreateAsync("/ttl", null, options, TestContext.Current.CancellationToken);
        });
        Assert.Throws<NotSupportedException>(() =>
        {
            _ = disabled.MultiAsync(
                [
                    ZooKeeperWriteOperation.Create(
                        "/ordinary",
                        null,
                        new CreateOptions([ZooKeeperAcl.OpenUnsafe])
                    ),
                    descriptor,
                ],
                TestContext.Current.CancellationToken
            );
        });
        Assert.Equal(0, peer.Requests.Count);
        Assert.Equal(0, disabled.Manager.Scheduler.OwnedBytes);
    }

    [Theory(Timeout = TestTimeoutMilliseconds)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CanceledTtlCreatePreservesOutcomeAndDrainsLateReply(bool sent)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken
        );
        Task? blocker = null;
        ReceivedPacket? blocked = null;
        if (!sent)
        {
            blocker = Client.SyncAsync("/block", TestContext.Current.CancellationToken);
            blocked = await SessionTestSupport.NextAsync(_peer).ConfigureAwait(true);
        }
        var pending = Client.CreateAsync(
            "/seq-",
            null,
            Options(CreateMode.PersistentSequentialWithTtl, TestTtlMilliseconds),
            stop.Token
        );
        ReceivedPacket? packet = sent
            ? await SessionTestSupport.NextAsync(_peer).ConfigureAwait(true)
            : null;
        await stop.CancelAsync().ConfigureAwait(true);
        var failure = await Assert
            .ThrowsAsync<ZooKeeperOperationCanceledException>(() => pending)
            .ConfigureAwait(true);
        Assert.Equal(sent ? RequestOutcome.Unknown : RequestOutcome.NotSent, failure.Outcome);
        Assert.Equal(ZooKeeperOperation.CreateTtl, failure.Request.Operation);
        Assert.True(pending.IsCanceled);
        if (packet is not null)
            InjectCreates(packet, false, "/app/seq-0000000042");
        else
        {
            _peer.Reply(blocked!);
            await blocker!.ConfigureAwait(true);
        }
        var next = Client.SyncAsync("/after", TestContext.Current.CancellationToken);
        _peer.Reply(await SessionTestSupport.NextAsync(_peer).ConfigureAwait(true));
        await next.ConfigureAwait(true);
        Assert.Equal(0, _peer.Requests.Count);
        Assert.Equal(0, Client.Manager.Scheduler.OwnedBytes);
    }

    [Fact(Timeout = TestTimeoutMilliseconds)]
    public async Task ExplicitlyEnabledClientPreservesUnimplementedServerError()
    {
        var pending = Client.CreateAsync(
            "/ttl",
            null,
            Options(CreateMode.PersistentWithTtl, TestTtlMilliseconds),
            TestContext.Current.CancellationToken
        );
        var packet = await SessionTestSupport.NextAsync(_peer).ConfigureAwait(true);
        _peer.Reply(packet, (int)ZooKeeperErrorCode.Unimplemented);
        var failure = await Assert
            .ThrowsAsync<ZooKeeperException>(() => pending)
            .ConfigureAwait(true);
        Assert.Equal(ZooKeeperErrorCode.Unimplemented, failure.KnownErrorCode);
        Assert.Equal(RequestOutcome.Rejected, failure.Outcome);
        Assert.Equal(ZooKeeperOperation.CreateTtl, failure.Request.Operation);
        Assert.Equal("/ttl", failure.Request.Path);
        Assert.Equal(packet.Xid, failure.Request.RequestId);
        Assert.Equal(ZooKeeperSessionState.Connected, Client.Session.State);
    }

    [Fact]
    public void EnabledClientStillRejectsOversizedDataBeforeSending()
    {
        var options = Options(CreateMode.PersistentWithTtl, TestTtlMilliseconds);
        byte[] data = new byte[OversizedDataBytes];
        Assert.Throws<InvalidDataException>(() =>
        {
            _ = Client.CreateAsync("/ttl", data, options, TestContext.Current.CancellationToken);
        });
        Assert.Throws<InvalidDataException>(() =>
        {
            _ = Client.MultiAsync(
                [ZooKeeperWriteOperation.Create("/ttl", data, options)],
                TestContext.Current.CancellationToken
            );
        });
        Assert.Equal(0, _peer.Requests.Count);
        Assert.Equal(0, Client.Manager.Scheduler.OwnedBytes);
    }

    private static CreateOptions Options(CreateMode mode, long milliseconds) =>
        new(
            [ZooKeeperAcl.OpenUnsafe],
            mode,
            TimeSpan.FromTicks(milliseconds * TimeSpan.TicksPerMillisecond)
        );

    private static void AssertCreateBody(
        ref JuteReader input,
        string path,
        CreateMode mode,
        long milliseconds
    )
    {
        Assert.Equal(path, input.ReadString());
        byte[] data = Assert.IsType<byte[]>(input.ReadBuffer());
        Assert.Equal((byte)1, Assert.Single(data));
        Assert.Equal(1, input.ReadInt32());
        Assert.Equal((int)ZooKeeperPermissions.All, input.ReadInt32());
        Assert.Equal("world", input.ReadString());
        Assert.Equal("anyone", input.ReadString());
        Assert.Equal((int)mode, input.ReadInt32());
        Assert.Equal(milliseconds, input.ReadInt64());
    }

    private void InjectCreates(ReceivedPacket packet, bool multi, params string[] paths)
    {
        var bytes = new byte[ReplyBufferBytes];
        var writer = new JuteWriter(bytes, ProtocolFixtures.Limits);
        writer.WriteReplyHeader(new ReplyHeader(packet.Xid, ReplyZxid, SuccessErrorCode));
        foreach (string path in paths)
        {
            if (multi)
            {
                writer.WriteInt32((int)ZooKeeperOperation.CreateWithStat);
                writer.WriteBoolean(false);
                writer.WriteInt32(SuccessErrorCode);
            }
            writer.WriteString(path);
            writer.WriteStat(ProtocolFixtures.Stat);
        }
        if (multi)
        {
            writer.WriteInt32(MultiSentinel);
            writer.WriteBoolean(true);
            writer.WriteInt32(MultiSentinel);
        }
        _peer.Inject(bytes[..writer.WrittenCount]);
    }
}
