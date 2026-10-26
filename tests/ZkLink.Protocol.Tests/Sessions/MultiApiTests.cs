using ZkLink.Protocol;

namespace ZkLink.Protocol.Tests.Sessions;

public sealed class MultiApiTests : IAsyncLifetime
{
    private readonly SessionFixture _fixture = new();
    private readonly SessionPeer _peer = new() { ReplyApplications = false };
    private static readonly CreateOptions Create = new([ZooKeeperAcl.OpenUnsafe]);
    private ZooKeeperClient Client => _fixture.Client!;

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
    [Trait("Acceptance", "T18")]
    public async Task WriteResultsKeepTypesOrderStatAndChrootPaths()
    {
        var pending = Client.MultiAsync(
            [
                ZooKeeperWriteOperation.Create("/created", null, Create),
                ZooKeeperWriteOperation.SetData(
                    "/data",
                    ReadOnlyMemory<byte>.Empty,
                    ZNodeVersion.Any
                ),
                ZooKeeperWriteOperation.Delete("/deleted", ZNodeVersion.Exact(2)),
                ZooKeeperWriteOperation.Check("/checked", ZNodeVersion.Exact(3)),
            ],
            TestContext.Current.CancellationToken
        );
        var packet = await SessionTestSupport.NextAsync(_peer).ConfigureAwait(true);
        Assert.Equal(14, packet.Opcode);
        Inject(
            packet,
            [
                new(15, 0, new PathResult("/app/created", ProtocolFixtures.Stat)),
                new(5, 0, new StatResult(ProtocolFixtures.Stat)),
                new(2, 0, new EmptyResult()),
                new(13, 0, new EmptyResult()),
            ]
        );
        IReadOnlyList<ZooKeeperWriteResult> result = await pending.ConfigureAwait(true);
        Assert.Equal(4, result.Count);
        CreateResult created = Assert.IsType<ZooKeeperCreatedResult>(result[0]).Value;
        Assert.Equal("/created", created.Path);
        Assert.Equal(5, created.Stat.Version);
        Assert.Equal(7, Assert.IsType<ZooKeeperDataSetResult>(result[1]).Stat.Aversion);
        Assert.IsType<ZooKeeperDeletedResult>(result[2]);
        Assert.IsType<ZooKeeperCheckedResult>(result[3]);
        Assert.True(((IList<ZooKeeperWriteResult>)result).IsReadOnly);
        Assert.Equal(0, Client.Manager.Scheduler.OwnedBytes);
    }

    [Theory(Timeout = 10000)]
    [InlineData(-103)]
    [InlineData(-999)]
    [Trait("Acceptance", "T18")]
    public async Task InnerWriteRejectionRetainsAllDiagnosticsAndAssignedRequestMetadata(
        int errorCode
    )
    {
        var pending = Client.MultiAsync(
            [
                ZooKeeperWriteOperation.Create("/first", null, Create),
                ZooKeeperWriteOperation.Check("/second", ZNodeVersion.Any),
                ZooKeeperWriteOperation.Delete("/third", ZNodeVersion.Any),
            ],
            TestContext.Current.CancellationToken
        );
        var packet = await SessionTestSupport.NextAsync(_peer).ConfigureAwait(true);
        Inject(
            packet,
            [
                new(15, 0, new PathResult("/app/first", ProtocolFixtures.Stat)),
                new(-1, errorCode, new ErrorResult(errorCode)),
                new(-1, -2, new ErrorResult(-2)),
            ]
        );
        ZooKeeperMultiException failure = await Assert
            .ThrowsAsync<ZooKeeperMultiException>(() => pending)
            .ConfigureAwait(true);
        Assert.Equal(RequestOutcome.Rejected, failure.Outcome);
        Assert.Equal(errorCode, failure.ErrorCode);
        Assert.Equal(1, failure.FailedMemberIndex);
        Assert.Equal(packet.Xid, failure.Request.RequestId);
        Assert.NotNull(failure.Request.Endpoint);
        Assert.Equal(3, failure.Diagnostics.Count);
        Assert.Equal("/first", failure.Diagnostics[0].Path);
        Assert.NotNull(failure.Diagnostics[0].Stat);
        Assert.Equal(0, failure.Diagnostics[0].ErrorCode);
        Assert.Equal(-1, failure.Diagnostics[1].Opcode);
        Assert.Equal(-2, failure.Diagnostics[2].ErrorCode);
        Assert.Equal(ZooKeeperSessionState.Connected, Client.Session.State);
    }

    [Fact(Timeout = 10000)]
    public async Task ReadResultsPreserveNullEmptyChildrenAndUnknownMemberErrors()
    {
        var pending = Client.MultiReadAsync(
            [
                ZooKeeperReadOperation.GetData("/null"),
                ZooKeeperReadOperation.GetData("/empty"),
                ZooKeeperReadOperation.GetChildren("/"),
                ZooKeeperReadOperation.GetData("/error"),
            ],
            TestContext.Current.CancellationToken
        );
        var packet = await SessionTestSupport.NextAsync(_peer).ConfigureAwait(true);
        Assert.Equal(22, packet.Opcode);
        var input = new JuteReader(packet.Bytes, ProtocolFixtures.Limits);
        input.ReadRequestHeader();
        foreach (string path in new[] { "/app/null", "/app/empty", "/app", "/app/error" })
        {
            int opcode = input.ReadInt32();
            Assert.True(opcode is 4 or 8);
            Assert.False(input.ReadBoolean());
            Assert.Equal(-1, input.ReadInt32());
            Assert.Equal(path, input.ReadString());
            Assert.False(input.ReadBoolean());
        }
        Inject(
            packet,
            [
                new(4, 0, new DataResult(null, ProtocolFixtures.Stat)),
                new(4, 0, new DataResult([], ProtocolFixtures.Stat)),
                new(8, 0, new StringsResult(["child"])),
                new(-1, -999, new ErrorResult(-999)),
            ]
        );
        IReadOnlyList<ZooKeeperReadResult> result = await pending.ConfigureAwait(true);
        Assert.Null(Assert.IsType<ZooKeeperDataReadResult>(result[0]).Value.Data);
        Assert.Empty(Assert.IsType<ZooKeeperDataReadResult>(result[1]).Value.Data!.Value.ToArray());
        Assert.Equal(
            "child",
            Assert.Single(Assert.IsType<ZooKeeperChildrenReadResult>(result[2]).Value)
        );
        var error = Assert.IsType<ZooKeeperReadErrorResult>(result[3]);
        Assert.Equal(-999, error.ErrorCode);
        Assert.Null(error.KnownErrorCode);
        Assert.True(((IList<ZooKeeperReadResult>)result).IsReadOnly);
    }

    [Fact(Timeout = 10000)]
    [Trait("Acceptance", "T11")]
    public async Task BatchListsAndPayloadsAreCapturedSynchronouslyBehindInFlightWork()
    {
        var blocker = Client.ExistsAsync("/blocker", TestContext.Current.CancellationToken);
        var blocked = await SessionTestSupport.NextAsync(_peer).ConfigureAwait(true);
        byte[] data = [1];
        ZooKeeperWriteOperation update = ZooKeeperWriteOperation.SetData(
            "/data",
            data,
            ZNodeVersion.Exact(int.MinValue)
        );
        data[0] = 2; // Factories retain descriptors; the invocation is the snapshot boundary.
        var operations = new List<ZooKeeperWriteOperation> { update };
        var pending = Client.MultiAsync(operations, TestContext.Current.CancellationToken);
        data[0] = 9;
        operations.Clear();
        _peer.Reply(blocked, -101);
        await blocker.ConfigureAwait(true);
        var packet = await SessionTestSupport.NextAsync(_peer).ConfigureAwait(true);
        var input = new JuteReader(packet.Bytes, ProtocolFixtures.Limits);
        input.ReadRequestHeader();
        Assert.Equal(5, input.ReadInt32());
        input.ReadBoolean();
        input.ReadInt32();
        Assert.Equal("/app/data", input.ReadString());
        Assert.Equal(new byte[] { 2 }, input.ReadBuffer());
        Assert.Equal(int.MinValue, input.ReadInt32());
        Inject(packet, [new(5, 0, new StatResult(ProtocolFixtures.Stat))]);
        await pending.ConfigureAwait(true);
    }

    [Theory(Timeout = 10000)]
    [InlineData(false, RequestOutcome.NotSent)]
    [InlineData(true, RequestOutcome.Unknown)]
    [Trait("Acceptance", "T12")]
    public async Task CancellationPreservesWholeTransactionOutcomeAndDrainsLateReplies(
        bool sent,
        RequestOutcome outcome
    )
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken
        );
        Task<ZNodeStat?>? blocker = null;
        ReceivedPacket? blocked = null;
        if (!sent)
        {
            blocker = Client.ExistsAsync("/blocker", TestContext.Current.CancellationToken);
            blocked = await SessionTestSupport.NextAsync(_peer).ConfigureAwait(true);
        }
        var pending = Client.MultiAsync(
            [ZooKeeperWriteOperation.Check("/check", ZNodeVersion.Any)],
            stop.Token
        );
        ReceivedPacket? packet = sent
            ? await SessionTestSupport.NextAsync(_peer).ConfigureAwait(true)
            : null;
        await stop.CancelAsync().ConfigureAwait(true);
        ZooKeeperOperationCanceledException failure = await Assert
            .ThrowsAsync<ZooKeeperOperationCanceledException>(() => pending)
            .ConfigureAwait(true);
        Assert.Equal(outcome, failure.Outcome);
        Assert.True(pending.IsCanceled);
        if (packet is not null)
            Inject(packet, [new(13, 0, new EmptyResult())]);
        else
        {
            _peer.Reply(blocked!, -101);
            await blocker!.ConfigureAwait(true);
        }
        var next = Client.ExistsAsync("/later", TestContext.Current.CancellationToken);
        var nextPacket = await SessionTestSupport.NextAsync(_peer).ConfigureAwait(true);
        Assert.Equal(3, nextPacket.Opcode);
        _peer.Reply(nextPacket, -101);
        await next.ConfigureAwait(true);
        Assert.Equal(0, _peer.Requests.Count);
        Assert.Equal(0, Client.Manager.Scheduler.OwnedBytes);
    }

    [Theory(Timeout = 10000)]
    [InlineData(-103, RequestOutcome.Rejected)]
    [InlineData(-999, RequestOutcome.Unknown)]
    public async Task OuterErrorsRemainRequestFailuresWithoutFabricatedMemberDiagnostics(
        int code,
        RequestOutcome outcome
    )
    {
        var pending = Client.MultiAsync(
            [ZooKeeperWriteOperation.Check("/check", ZNodeVersion.Any)],
            TestContext.Current.CancellationToken
        );
        _peer.Reply(await SessionTestSupport.NextAsync(_peer).ConfigureAwait(true), code);
        ZooKeeperException failure = await Assert
            .ThrowsAsync<ZooKeeperException>(() => pending)
            .ConfigureAwait(true);
        Assert.Equal(outcome, failure.Outcome);
        Assert.Equal(code, failure.ErrorCode);
    }

    [Theory(Timeout = 10000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ZeroErrorPlaceholderWithoutAResultFaultsTheProtocol(bool readOnly)
    {
        Task pending = readOnly
            ? Client.MultiReadAsync(
                [ZooKeeperReadOperation.GetData("/node")],
                TestContext.Current.CancellationToken
            )
            : Client.MultiAsync(
                [ZooKeeperWriteOperation.Check("/node", ZNodeVersion.Any)],
                TestContext.Current.CancellationToken
            );
        var packet = await SessionTestSupport.NextAsync(_peer).ConfigureAwait(true);
        Inject(packet, [new(-1, 0, new ErrorResult(0))]);
        await SessionTestSupport.Bounded(Client.Manager.Completion).ConfigureAwait(true);
        await Assert.ThrowsAsync<ZooKeeperException>(() => pending).ConfigureAwait(true);
        Assert.Equal(ZooKeeperSessionState.Faulted, Client.Session.State);
    }

    [Fact(Timeout = 10000)]
    public async Task InnerSessionExpiryRevokesReadinessBeforeCompletingTheObservedFailure()
    {
        var pending = Client.MultiAsync(
            [ZooKeeperWriteOperation.Check("/node", ZNodeVersion.Any)],
            TestContext.Current.CancellationToken
        );
        var packet = await SessionTestSupport.NextAsync(_peer).ConfigureAwait(true);
        Inject(packet, [new(-1, -112, new ErrorResult(-112))]);
        ZooKeeperMultiException failure = await Assert
            .ThrowsAsync<ZooKeeperMultiException>(() => pending)
            .ConfigureAwait(true);
        Assert.Equal(-112, failure.ErrorCode);
        Assert.Equal(ZooKeeperSessionState.Expired, Client.Session.State);
    }

    [Fact]
    public void EntireInvalidOrOversizedBatchesFailBeforeSendingAnyMember()
    {
        Assert.Throws<ArgumentException>(() =>
        {
            _ = Client.MultiAsync([], TestContext.Current.CancellationToken);
        });
        Assert.Throws<ArgumentException>(() =>
        {
            _ = Client.MultiReadAsync([null!], TestContext.Current.CancellationToken);
        });
        Assert.Throws<ArgumentException>(() =>
            ZooKeeperWriteOperation.Check("relative", ZNodeVersion.Any)
        );
        var ttl = ZooKeeperWriteOperation.Create(
            "/ttl",
            null,
            new CreateOptions(Create.Acl, CreateMode.PersistentWithTtl, TimeSpan.FromSeconds(1))
        );
        Assert.Throws<NotSupportedException>(() =>
        {
            _ = Client.MultiAsync([ttl], TestContext.Current.CancellationToken);
        });
        byte[] oversized = new byte[1_000_001];
        Assert.Throws<InvalidDataException>(() =>
        {
            _ = Client.MultiAsync(
                [
                    ZooKeeperWriteOperation.Check("/valid", ZNodeVersion.Any),
                    ZooKeeperWriteOperation.SetData("/data", oversized, ZNodeVersion.Any),
                ],
                TestContext.Current.CancellationToken
            );
        });
        byte[] individuallyValid = new byte[600_000];
        Assert.Throws<InvalidDataException>(() =>
        {
            _ = Client.MultiAsync(
                [
                    ZooKeeperWriteOperation.SetData("/a", individuallyValid, ZNodeVersion.Any),
                    ZooKeeperWriteOperation.SetData("/b", individuallyValid, ZNodeVersion.Any),
                ],
                TestContext.Current.CancellationToken
            );
        });
        Assert.Equal(0, _peer.Requests.Count);
    }

    private void Inject(ReceivedPacket packet, IReadOnlyList<WireMultiMember> members)
    {
        var bytes = new byte[4096];
        var writer = new JuteWriter(bytes, ProtocolFixtures.Limits);
        writer.WriteReplyHeader(new ReplyHeader(packet.Xid, 12, 0));
        foreach (var member in members)
        {
            writer.WriteInt32(member.Opcode);
            writer.WriteBoolean(false);
            writer.WriteInt32(member.Error);
            switch (member.Result)
            {
                case PathResult path:
                    writer.WriteString(path.Path);
                    writer.WriteStat(path.Stat!.Value);
                    break;
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
                    break;
                case ErrorResult error:
                    writer.WriteInt32(error.Error);
                    break;
                case EmptyResult:
                    break;
                default:
                    throw new InvalidOperationException("Unknown test result.");
            }
        }
        writer.WriteInt32(-1);
        writer.WriteBoolean(true);
        writer.WriteInt32(-1);
        _peer.Inject(bytes[..writer.WrittenCount]);
    }
}
