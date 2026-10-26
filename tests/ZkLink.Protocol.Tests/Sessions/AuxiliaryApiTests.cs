using ZkLink.Protocol;

namespace ZkLink.Protocol.Tests.Sessions;

public sealed class AuxiliaryApiTests : IAsyncLifetime
{
    private const int TestTimeoutMilliseconds = 10_000;
    private const int MaxCollectionElements = 8;
    private const int ExcessiveCollectionElements = MaxCollectionElements + 1;
    private const int InvalidCountCase = 0;
    private const int ExcessiveCollectionCase = 1;
    private const int InvalidStringCase = 2;
    private const int NullVectorOrNegativeCount = -1;
    private const int UnrecognizedErrorCode = -999;
    private const int MalformedReplyBufferBytes = 256;
    private const int ReplyBufferBytes = 4096;
    private const int ReplyZxid = 12;
    private const int SuccessErrorCode = 0;
    private readonly SessionFixture _fixture = new();
    private readonly SessionPeer _peer = new() { ReplyApplications = false };
    private ZooKeeperClient Client => _fixture.Client!;
    private static readonly string[] TextualMatches = ["/node", "/node2", "/node/child"];
    private static readonly string[] RootMatches = ["/", "/inside"];

    public async ValueTask InitializeAsync()
    {
        await _fixture
            .ConnectAsync(
                _peer,
                SessionFixture.Options(
                    inFlight: 1,
                    connection: "first:2181/app",
                    collections: MaxCollectionElements
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

    [Fact(Timeout = TestTimeoutMilliseconds)]
    [Trait("Acceptance", "T22")]
    public async Task EphemeralResultsPreserveTextualMatchesButFilterTheChrootBoundary()
    {
        var pending = Client.GetEphemeralsAsync("/node", TestContext.Current.CancellationToken);
        var packet = await NextAsync(ZooKeeperOperation.GetEphemerals, "/app/node")
            .ConfigureAwait(true);
        Inject(
            packet,
            new StringsResult([
                "/app/node",
                "/app/node2",
                "/app/node/child",
                "/app2/node",
                "/elsewhere",
            ])
        );
        IReadOnlyList<string> result = await pending.ConfigureAwait(true);
        Assert.Equal(TextualMatches, result);
        Assert.Throws<NotSupportedException>(() => ((IList<string>)result)[0] = "/changed");

        pending = Client.GetEphemeralsAsync("/", TestContext.Current.CancellationToken);
        packet = await NextAsync(ZooKeeperOperation.GetEphemerals, "/app").ConfigureAwait(true);
        Inject(packet, new StringsResult(["/app", "/app/inside", "/app2/outside"]));
        Assert.Equal(RootMatches, await pending.ConfigureAwait(true));

        pending = Client.GetEphemeralsAsync("/nonexistent", TestContext.Current.CancellationToken);
        packet = await NextAsync(ZooKeeperOperation.GetEphemerals, "/app/nonexistent")
            .ConfigureAwait(true);
        Inject(packet, new StringsResult([]));
        Assert.Empty(await pending.ConfigureAwait(true));
        Assert.Equal(TextualMatches, result);
    }

    [Fact(Timeout = TestTimeoutMilliseconds)]
    [Trait("Acceptance", "T22")]
    public async Task CountUsesOnePathRequestAndPreservesTheNonnegativeInt32Range()
    {
        foreach (int count in new[] { 0, 42, int.MaxValue })
        {
            var pending = Client.GetAllChildrenNumberAsync(
                "/",
                TestContext.Current.CancellationToken
            );
            var packet = await NextAsync(ZooKeeperOperation.GetAllChildrenNumber, "/app")
                .ConfigureAwait(true);
            Inject(packet, new CountResult(count));
            Assert.Equal(count, await pending.ConfigureAwait(true));
        }
    }

    [Fact(Timeout = TestTimeoutMilliseconds)]
    [Trait("Acceptance", "T22")]
    public async Task IdentityRequestIsBodylessAndResultsAreOpaqueImmutableSnapshots()
    {
        var pending = Client.WhoAmIAsync(TestContext.Current.CancellationToken);
        var packet = await NextAsync(ZooKeeperOperation.WhoAmI).ConfigureAwait(true);
        Inject(
            packet,
            new IdentityResult([
                new("custom", "opaque:秘密"),
                new("digest", ""),
                new("ip", "127.0.0.1"),
            ])
        );
        IReadOnlyList<ZooKeeperClientIdentity> identities = await pending.ConfigureAwait(true);
        Assert.Equal(3, identities.Count);
        Assert.Equal("custom", identities[0].Scheme);
        Assert.Equal("opaque:秘密", identities[0].User);
        Assert.Equal("", identities[1].User);
        Assert.Equal("127.0.0.1", identities[2].User);
        Assert.DoesNotContain("opaque:秘密", identities[0].ToString(), StringComparison.Ordinal);
        Assert.Throws<NotSupportedException>(() =>
            ((IList<ZooKeeperClientIdentity>)identities).Clear()
        );
        pending = Client.WhoAmIAsync(TestContext.Current.CancellationToken);
        packet = await NextAsync(ZooKeeperOperation.WhoAmI).ConfigureAwait(true);
        Inject(packet, new IdentityResult([]));
        Assert.Empty(await pending.ConfigureAwait(true));
        Assert.Equal("opaque:秘密", identities[0].User);
    }

    [Theory(Timeout = TestTimeoutMilliseconds)]
    [InlineData(
        ZooKeeperOperation.GetEphemerals,
        (int)ZooKeeperErrorCode.Unimplemented,
        RequestOutcome.Rejected
    )]
    [InlineData(
        ZooKeeperOperation.GetAllChildrenNumber,
        (int)ZooKeeperErrorCode.NoNode,
        RequestOutcome.Rejected
    )]
    [InlineData(
        ZooKeeperOperation.GetAllChildrenNumber,
        (int)ZooKeeperErrorCode.NoAuth,
        RequestOutcome.Rejected
    )]
    [InlineData(ZooKeeperOperation.WhoAmI, UnrecognizedErrorCode, RequestOutcome.Unknown)]
    public async Task ServerErrorsPreserveOperationPathXidAndEndpoint(
        ZooKeeperOperation operation,
        int code,
        RequestOutcome outcome
    )
    {
        Task pending = Submit(operation, TestContext.Current.CancellationToken);
        var packet = await NextAsync(
                operation,
                operation == ZooKeeperOperation.WhoAmI ? null : "/app/node"
            )
            .ConfigureAwait(true);
        _peer.Reply(packet, code);
        ZooKeeperException failure = await Assert
            .ThrowsAsync<ZooKeeperException>(() => pending)
            .ConfigureAwait(true);
        Assert.Equal(code, failure.ErrorCode);
        Assert.Equal(outcome, failure.Outcome);
        Assert.Equal(operation, failure.Request.Operation);
        Assert.Equal(packet.Xid, failure.Request.RequestId);
        Assert.NotNull(failure.Request.Endpoint);
        Assert.Equal(operation == ZooKeeperOperation.WhoAmI ? null : "/node", failure.Request.Path);
        Assert.Equal(ZooKeeperSessionState.Connected, Client.Session.State);
        await ReadFollowingCountAsync().ConfigureAwait(true);
    }

    [Theory(Timeout = TestTimeoutMilliseconds)]
    [InlineData(ZooKeeperOperation.GetEphemerals, false)]
    [InlineData(ZooKeeperOperation.GetAllChildrenNumber, false)]
    [InlineData(ZooKeeperOperation.WhoAmI, false)]
    [InlineData(ZooKeeperOperation.GetEphemerals, true)]
    [InlineData(ZooKeeperOperation.GetAllChildrenNumber, true)]
    [InlineData(ZooKeeperOperation.WhoAmI, true)]
    public async Task CancellationRetainsTheCorrectOutcomeAndDrainsSentReplies(
        ZooKeeperOperation operation,
        bool sent
    )
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken
        );
        Task<int>? blocker = null;
        ReceivedPacket? blocked = null;
        if (!sent)
        {
            blocker = Client.GetAllChildrenNumberAsync(
                "/block",
                TestContext.Current.CancellationToken
            );
            blocked = await NextAsync(ZooKeeperOperation.GetAllChildrenNumber, "/app/block")
                .ConfigureAwait(true);
        }
        Task pending = Submit(operation, stop.Token);
        ReceivedPacket? packet = sent
            ? await SessionTestSupport.NextAsync(_peer).ConfigureAwait(true)
            : null;
        await stop.CancelAsync().ConfigureAwait(true);
        ZooKeeperOperationCanceledException failure = await Assert
            .ThrowsAsync<ZooKeeperOperationCanceledException>(() => pending)
            .ConfigureAwait(true);
        Assert.Equal(sent ? RequestOutcome.Unknown : RequestOutcome.NotSent, failure.Outcome);
        Assert.Equal(operation, failure.Request.Operation);
        Assert.True(pending.IsCanceled);
        if (packet is not null)
            Inject(
                packet,
                operation switch
                {
                    ZooKeeperOperation.GetEphemerals => new StringsResult(["/app/node"]),
                    ZooKeeperOperation.GetAllChildrenNumber => new CountResult(7),
                    _ => new IdentityResult([new("ip", "127.0.0.1")]),
                }
            );
        else
        {
            Inject(blocked!, new CountResult(0));
            await blocker!.ConfigureAwait(true);
        }
        await ReadFollowingCountAsync().ConfigureAwait(true);
        Assert.Equal(0, _peer.Requests.Count);
        Assert.Equal(0, Client.Manager.Scheduler.OwnedBytes);
    }

    [Theory(Timeout = TestTimeoutMilliseconds)]
    [InlineData(ZooKeeperOperation.GetEphemerals, InvalidCountCase)] // Null vector.
    [InlineData(ZooKeeperOperation.GetEphemerals, ExcessiveCollectionCase)] // Excessive vector count.
    [InlineData(ZooKeeperOperation.GetEphemerals, InvalidStringCase)] // Relative path.
    [InlineData(ZooKeeperOperation.GetAllChildrenNumber, InvalidCountCase)] // Negative descendant count.
    [InlineData(ZooKeeperOperation.WhoAmI, InvalidCountCase)] // Null vector.
    [InlineData(ZooKeeperOperation.WhoAmI, ExcessiveCollectionCase)] // Excessive vector count.
    [InlineData(ZooKeeperOperation.WhoAmI, InvalidStringCase)] // Empty identity scheme.
    public async Task MalformedAuxiliaryRepliesFaultTheProtocol(
        ZooKeeperOperation operation,
        int kind
    )
    {
        Task pending = Submit(operation, TestContext.Current.CancellationToken);
        var packet = await SessionTestSupport.NextAsync(_peer).ConfigureAwait(true);
        var bytes = new byte[MalformedReplyBufferBytes];
        var writer = new JuteWriter(bytes, ProtocolFixtures.Limits);
        writer.WriteReplyHeader(new ReplyHeader(packet.Xid, ReplyZxid, SuccessErrorCode));
        if (kind == InvalidCountCase)
            writer.WriteInt32(NullVectorOrNegativeCount);
        else if (kind == ExcessiveCollectionCase)
        {
            // Otherwise valid entries fit the frame but exceed the configured collection limit.
            writer.WriteInt32(ExcessiveCollectionElements);
            for (var index = 0; index < ExcessiveCollectionElements; index++)
            {
                writer.WriteString(
                    operation == ZooKeeperOperation.GetEphemerals ? "/app/node" : "s"
                );
                if (operation == ZooKeeperOperation.WhoAmI)
                    writer.WriteString("");
            }
        }
        else
        {
            writer.WriteInt32(1);
            writer.WriteString(operation == ZooKeeperOperation.GetEphemerals ? "relative" : "");
            if (operation == ZooKeeperOperation.WhoAmI)
                writer.WriteString("user");
        }
        _peer.Inject(bytes[..writer.WrittenCount]);
        await SessionTestSupport.Bounded(Client.Manager.Completion).ConfigureAwait(true);
        await Assert.ThrowsAsync<ZooKeeperException>(() => pending).ConfigureAwait(true);
        Assert.Equal(ZooKeeperSessionState.Faulted, Client.Session.State);
    }

    [Fact]
    public void InvalidAndOversizedPathsRejectBeforeSending()
    {
        Assert.Throws<ArgumentNullException>(() =>
        {
            _ = Client.GetEphemeralsAsync(null!, TestContext.Current.CancellationToken);
        });
        Assert.Throws<ArgumentException>(() =>
        {
            _ = Client.GetEphemeralsAsync("relative", TestContext.Current.CancellationToken);
        });
        Assert.Throws<ArgumentException>(() =>
        {
            _ = Client.GetAllChildrenNumberAsync("/bad/", TestContext.Current.CancellationToken);
        });
        string oversized = "/" + new string('a', ProtocolLimits.DefaultMaxFrameBytes);
        Assert.Throws<InvalidDataException>(() =>
        {
            _ = Client.GetEphemeralsAsync(oversized, TestContext.Current.CancellationToken);
        });
        Assert.Throws<InvalidDataException>(() =>
        {
            _ = Client.GetAllChildrenNumberAsync(oversized, TestContext.Current.CancellationToken);
        });
        Assert.Equal(0, _peer.Requests.Count);
        Assert.Equal(0, Client.Manager.Scheduler.OwnedBytes);
    }

    private Task Submit(ZooKeeperOperation operation, CancellationToken token) =>
        operation switch
        {
            ZooKeeperOperation.GetEphemerals => Client.GetEphemeralsAsync("/node", token),
            ZooKeeperOperation.GetAllChildrenNumber => Client.GetAllChildrenNumberAsync(
                "/node",
                token
            ),
            _ => Client.WhoAmIAsync(token),
        };

    private async Task<ReceivedPacket> NextAsync(ZooKeeperOperation operation, string? path = null)
    {
        var packet = await SessionTestSupport.NextAsync(_peer).ConfigureAwait(false);
        Assert.Equal((int)operation, packet.Opcode);
        var input = new JuteReader(packet.Bytes, ProtocolFixtures.Limits);
        input.ReadRequestHeader();
        if (path is not null)
            Assert.Equal(path, input.ReadString());
        input.EnsureEnd();
        return packet;
    }

    private async Task ReadFollowingCountAsync()
    {
        var next = Client.GetAllChildrenNumberAsync(
            "/later",
            TestContext.Current.CancellationToken
        );
        Inject(
            await NextAsync(ZooKeeperOperation.GetAllChildrenNumber, "/app/later")
                .ConfigureAwait(false),
            new CountResult(23)
        );
        Assert.Equal(23, await next.ConfigureAwait(false));
    }

    private void Inject(ReceivedPacket packet, WireResult result)
    {
        var bytes = new byte[ReplyBufferBytes];
        var writer = new JuteWriter(bytes, ProtocolFixtures.Limits);
        writer.WriteReplyHeader(new ReplyHeader(packet.Xid, ReplyZxid, SuccessErrorCode));
        switch (result)
        {
            case StringsResult strings:
                writer.WriteStringVector(strings.Values);
                break;
            case CountResult count:
                writer.WriteInt32(count.Count);
                break;
            case IdentityResult identities:
                writer.WriteInt32(identities.Identities.Length);
                foreach (var identity in identities.Identities)
                {
                    writer.WriteString(identity.Scheme);
                    writer.WriteString(identity.User);
                }
                break;
            default:
                throw new InvalidOperationException("Unknown test reply.");
        }
        _peer.Inject(bytes[..writer.WrittenCount]);
    }
}
