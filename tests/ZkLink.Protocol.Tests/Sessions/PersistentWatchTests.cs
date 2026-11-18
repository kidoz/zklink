using System.Globalization;
using ZkLink.Diagnostics;
using ZkLink.Protocol;
using ZkLink.Testing;
using ZkLink.Watches;

namespace ZkLink.Protocol.Tests.Sessions;

[Collection("Telemetry")]
public sealed class PersistentWatchTests : IAsyncLifetime
{
    private const int TestTimeoutMilliseconds = 10_000;
    private const int StressTimeoutMilliseconds = 60_000;
    private const int PacketBytes = 4096;
    private const long Watermark = 12;
    private readonly SessionFixture _fixture = new();
    private readonly SessionPeer _peer = new() { ReplyApplications = false };
    private ZooKeeperClient Client => _fixture.Client!;

    public async ValueTask InitializeAsync()
    {
        await _fixture
            .ConnectAsync(
                _peer,
                SessionFixture.Options(
                    events: 20_000,
                    connection: "first:2181,second:2181/app",
                    profile: ZooKeeperCompatibilityProfile.ZooKeeper39
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
    [InlineData(PersistentWatchMode.Persistent)]
    [InlineData(PersistentWatchMode.PersistentRecursive)]
    [Trait("Acceptance", "T25")]
    public async Task RegistrationPrecedesEventsAndRemainsActiveAcrossDeletionAndRecreation(
        PersistentWatchMode mode
    )
    {
        var pending = Client.AddWatchAsync("/node", mode, TestContext.Current.CancellationToken);
        var packet = await SessionTestSupport.NextAsync(_peer).ConfigureAwait(true);
        AssertWatchRequest(packet, ZooKeeperOperation.AddWatch, "/app/node", (int)mode);
        _peer.Reply(packet);
        Notify(_peer, ZooKeeperWatchEventKind.NodeCreated, "/app/node");
        Notify(_peer, ZooKeeperWatchEventKind.NodeDeleted, "/app/node");
        Notify(_peer, ZooKeeperWatchEventKind.NodeCreated, "/app/node");
        await BarrierAsync().ConfigureAwait(true);
        var watch = await pending.ConfigureAwait(true);
        Assert.Equal(
            mode == PersistentWatchMode.Persistent
                ? ZooKeeperWatchMode.Persistent
                : ZooKeeperWatchMode.PersistentRecursive,
            watch.Mode
        );
        var reader = watch
            .ReadEventsAsync(TestContext.Current.CancellationToken)
            .GetAsyncEnumerator(TestContext.Current.CancellationToken);
        await using var lifetime = reader.ConfigureAwait(true);
        foreach (
            var kind in new[]
            {
                ZooKeeperWatchEventKind.NodeCreated,
                ZooKeeperWatchEventKind.NodeDeleted,
                ZooKeeperWatchEventKind.NodeCreated,
            }
        )
        {
            Assert.True(await reader.MoveNextAsync().ConfigureAwait(true));
            Assert.Equal(kind, reader.Current.Kind);
            Assert.Equal("/node", reader.Current.Path);
            Assert.Equal(watch.RegistrationId, reader.Current.RegistrationId);
            Assert.Equal(Client.Session.ConnectionGeneration, reader.Current.ConnectionGeneration);
            Assert.Equal(Watermark, reader.Current.Zxid);
        }
        Assert.Equal(1, Client.Manager.WatchCount);
        await watch.DisposeAsync().ConfigureAwait(true);
        Assert.False(await reader.MoveNextAsync().ConfigureAwait(true));
    }

    [Fact(Timeout = TestTimeoutMilliseconds)]
    [Trait("Acceptance", "T25")]
    public async Task RecursiveRoutingRespectsBoundariesAndFiltersChildEventsEvenAtItsRoot()
    {
        var exact = await RegisterAsync(PersistentWatchMode.Persistent).ConfigureAwait(true);
        var recursive = await RegisterAsync(PersistentWatchMode.PersistentRecursive)
            .ConfigureAwait(true);
        var root = await RegisterAsync(PersistentWatchMode.PersistentRecursive, "/")
            .ConfigureAwait(true);
        Notify(_peer, ZooKeeperWatchEventKind.NodeChildrenChanged, "/app/node");
        Notify(_peer, ZooKeeperWatchEventKind.NodeChildrenChanged, "/app/node/child");
        Notify(_peer, ZooKeeperWatchEventKind.NodeDataChanged, "/app/node-other/child");
        Notify(_peer, ZooKeeperWatchEventKind.NodeCreated, "/app/node/child");
        Notify(_peer, ZooKeeperWatchEventKind.NodeDataChanged, "/app/node");
        await BarrierAsync().ConfigureAwait(true);
        Assert.Equal(2, exact.Queue.Reader.Count);
        Assert.Equal(2, recursive.Queue.Reader.Count);
        Assert.Equal(3, root.Queue.Reader.Count);
        var reader = recursive
            .ReadEventsAsync(TestContext.Current.CancellationToken)
            .GetAsyncEnumerator(TestContext.Current.CancellationToken);
        await using var lifetime = reader.ConfigureAwait(true);
        Assert.True(await reader.MoveNextAsync().ConfigureAwait(true));
        Assert.Equal("/node/child", reader.Current.Path);
        Assert.Equal(ZooKeeperWatchEventKind.NodeCreated, reader.Current.Kind);
        Assert.True(await reader.MoveNextAsync().ConfigureAwait(true));
        Assert.Equal("/node", reader.Current.Path);
        Assert.Equal(ZooKeeperWatchEventKind.NodeDataChanged, reader.Current.Kind);
    }

    [Fact(Timeout = TestTimeoutMilliseconds)]
    public async Task OneShotAndPersistentCategoriesReceiveOnceAndRemovalDoesNotCrossCategories()
    {
        var exact = await RegisterAsync(PersistentWatchMode.Persistent).ConfigureAwait(true);
        var recursive = await RegisterAsync(PersistentWatchMode.PersistentRecursive)
            .ConfigureAwait(true);
        var read = Client.ExistsAndWatchAsync("/node", TestContext.Current.CancellationToken);
        _peer.Reply(
            await SessionTestSupport.NextAsync(_peer).ConfigureAwait(true),
            (int)ZooKeeperErrorCode.NoNode
        );
        var oneShot = (await read.ConfigureAwait(true)).Watch;
        Notify(_peer, ZooKeeperWatchEventKind.NodeCreated, "/app/node");
        Notify(_peer, ZooKeeperWatchEventKind.NodeDataChanged, "/app/node");
        await BarrierAsync().ConfigureAwait(true);
        Assert.Equal(1, oneShot.Queue.Reader.Count);
        Assert.Equal(2, exact.Queue.Reader.Count);
        Assert.Equal(2, recursive.Queue.Reader.Count);
        var removal = exact.RemoveAsync(
            WatchRemovalMode.ServerConfirmed,
            TestContext.Current.CancellationToken
        );
        var packet = await SessionTestSupport.NextAsync(_peer).ConfigureAwait(true);
        AssertWatchRequest(
            packet,
            ZooKeeperOperation.RemoveWatches,
            "/app/node",
            WatchProtocol.Persistent
        );
        _peer.Reply(packet);
        Assert.Equal(
            WatchRemovalStatus.ServerConfirmed,
            (await removal.ConfigureAwait(true)).Status
        );
        Notify(_peer, ZooKeeperWatchEventKind.NodeDeleted, "/app/node");
        await BarrierAsync().ConfigureAwait(true);
        Assert.Equal(3, recursive.Queue.Reader.Count);
        Assert.Equal(0, exact.Queue.Reader.Count);
    }

    [Theory(Timeout = TestTimeoutMilliseconds)]
    [InlineData(PersistentWatchMode.Persistent, WatchProtocol.Persistent)]
    [InlineData(PersistentWatchMode.PersistentRecursive, WatchProtocol.PersistentRecursive)]
    [Trait("Acceptance", "T29")]
    public async Task SharedRemovalRetainsTheOtherHandleAndLastRemovalUsesItsOwnCategory(
        PersistentWatchMode mode,
        int category
    )
    {
        var first = await RegisterAsync(mode).ConfigureAwait(true);
        var second = await RegisterAsync(mode).ConfigureAwait(true);
        var result = await first
            .RemoveAsync(WatchRemovalMode.ServerConfirmed, TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        Assert.Equal(WatchRemovalStatus.SharedRegistrationRetained, result.Status);
        Assert.Equal(0, _peer.Requests.Count);
        Notify(_peer, ZooKeeperWatchEventKind.NodeDataChanged, "/app/node");
        await BarrierAsync().ConfigureAwait(true);
        Assert.Equal(0, first.Queue.Reader.Count);
        Assert.Equal(1, second.Queue.Reader.Count);
        var removal = second.RemoveAsync(
            WatchRemovalMode.ServerConfirmed,
            TestContext.Current.CancellationToken
        );
        var packet = await SessionTestSupport.NextAsync(_peer).ConfigureAwait(true);
        AssertWatchRequest(packet, ZooKeeperOperation.RemoveWatches, "/app/node", category);
        _peer.Reply(packet, (int)ZooKeeperErrorCode.NoWatcher);
        Assert.Equal(WatchRemovalStatus.NoWatcher, (await removal.ConfigureAwait(true)).Status);
        Assert.Equal(0, Client.Manager.WatchCount);
    }

    [Theory(Timeout = TestTimeoutMilliseconds)]
    [InlineData(PersistentWatchMode.Persistent, WatchProtocol.Persistent)]
    [InlineData(PersistentWatchMode.PersistentRecursive, WatchProtocol.PersistentRecursive)]
    [Trait("Acceptance", "T28")]
    public async Task SentCanceledAddCleansLateSuccessInTheCorrectCategory(
        PersistentWatchMode mode,
        int category
    )
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken
        );
        var pending = Client.AddWatchAsync("/node", mode, stop.Token);
        var packet = await SessionTestSupport.NextAsync(_peer).ConfigureAwait(true);
        await stop.CancelAsync().ConfigureAwait(true);
        var error = await Assert
            .ThrowsAsync<ZooKeeperOperationCanceledException>(() => pending)
            .ConfigureAwait(true);
        Assert.Equal(RequestOutcome.Unknown, error.Outcome);
        _peer.Reply(packet);
        Notify(_peer, ZooKeeperWatchEventKind.NodeCreated, "/app/node");
        var cleanup = await SessionTestSupport.NextAsync(_peer).ConfigureAwait(true);
        AssertWatchRequest(cleanup, ZooKeeperOperation.RemoveWatches, "/app/node", category);
        _peer.Reply(cleanup);
        await BarrierAsync().ConfigureAwait(true);
        Assert.Equal(0, Client.Manager.WatchCount);
    }

    [Theory(Timeout = TestTimeoutMilliseconds)]
    [InlineData(
        ZooKeeperCompatibilityProfile.ZooKeeper38,
        PersistentWatchMode.Persistent,
        WatchProtocol.Any,
        null
    )]
    [InlineData(
        ZooKeeperCompatibilityProfile.ZooKeeper38,
        PersistentWatchMode.PersistentRecursive,
        WatchProtocol.Any,
        ZooKeeperErrorCode.NoAuth
    )]
    [InlineData(
        ZooKeeperCompatibilityProfile.ZooKeeper39,
        PersistentWatchMode.Persistent,
        WatchProtocol.Persistent,
        ZooKeeperErrorCode.NoAuth
    )]
    [InlineData(
        ZooKeeperCompatibilityProfile.ZooKeeper39,
        PersistentWatchMode.PersistentRecursive,
        WatchProtocol.PersistentRecursive,
        null
    )]
    [Trait("Acceptance", "T25")]
    public async Task MalformedAddAcknowledgmentCleansTheUnclaimedWatch(
        ZooKeeperCompatibilityProfile profile,
        PersistentWatchMode mode,
        int removalCategory,
        ZooKeeperErrorCode? bodyStatus
    )
    {
        var token = TestContext.Current.CancellationToken;
        using var peer = new SessionPeer { ReplyApplications = false };
        var fixture = new SessionFixture();
        await using var fixtureLifetime = fixture.ConfigureAwait(true);
        var client = await fixture
            .ConnectAsync(
                peer,
                SessionFixture.Options(inFlight: 1, connection: "first:2181/app", profile: profile),
                token
            )
            .ConfigureAwait(true);
        await SessionTestSupport.NextAsync(peer).ConfigureAwait(true);
        var generation = client.Session.ConnectionGeneration;
        const string counter = "zklink.protocol.rejected_replies";
        using var capture = new TelemetryCapture();
        await ZooKeeperTelemetry.FlushAsync(token).ConfigureAwait(true);
        capture.Collect();
        double before = capture.Value(counter);

        var add = client.AddWatchAsync("/node", mode, token);
        var packet = await SessionTestSupport.NextAsync(peer).ConfigureAwait(true);
        AssertWatchRequest(packet, ZooKeeperOperation.AddWatch, "/app/node", (int)mode);
        var next = client.SyncAsync("/next", token);
        var bytes = new byte[PacketBytes];
        var writer = new JuteWriter(bytes, ProtocolFixtures.Limits);
        writer.WriteReplyHeader(new ReplyHeader(packet.Xid, Watermark, WatchProtocol.Success));
        if (bodyStatus.HasValue)
        {
            writer.WriteInt32((int)bodyStatus.Value);
        }
        peer.Inject(bytes[..writer.WrittenCount]);

        var failure = await Assert
            .ThrowsAsync<ZooKeeperProtocolException>(() => add)
            .ConfigureAwait(true);
        Assert.Equal(RequestOutcome.Unknown, failure.Outcome);
        Assert.Equal(packet.Xid, failure.Request.RequestId);
        Assert.Equal(ZooKeeperOperation.AddWatch, failure.Request.Operation);
        Assert.Null(failure.ErrorCode);
        Assert.Equal(0, client.Manager.WatchCount);

        var following = new[]
        {
            await SessionTestSupport.NextAsync(peer).ConfigureAwait(true),
            await SessionTestSupport.NextAsync(peer).ConfigureAwait(true),
        };
        var cleanup = Assert.Single(
            following,
            request => request.Opcode == (int)ZooKeeperOperation.RemoveWatches
        );
        AssertWatchRequest(cleanup, ZooKeeperOperation.RemoveWatches, "/app/node", removalCategory);
        Assert.Single(following, request => request.Opcode == (int)ZooKeeperOperation.Sync);
        foreach (var request in following)
        {
            peer.Reply(request);
        }
        await next.ConfigureAwait(true);
        Assert.Equal(ZooKeeperSessionState.Connected, client.Session.State);
        Assert.Equal(generation, client.Session.ConnectionGeneration);
        capture.Collect();
        Assert.Equal(before + 1, capture.Value(counter));
    }

    [Theory(Timeout = TestTimeoutMilliseconds)]
    [InlineData(PersistentWatchMode.Persistent, "/node")]
    [InlineData(PersistentWatchMode.PersistentRecursive, "/node/child")]
    [Trait("Acceptance", "T25")]
    public async Task MalformedAddAcknowledgmentPreservesTheSharedRegistration(
        PersistentWatchMode mode,
        string eventPath
    )
    {
        var token = TestContext.Current.CancellationToken;
        var existing = await RegisterAsync(mode).ConfigureAwait(true);
        var add = Client.AddWatchAsync("/node", mode, token);
        var packet = await SessionTestSupport.NextAsync(_peer).ConfigureAwait(true);
        _peer.Inject(SessionPeer.Packet(packet.Xid));
        await Assert.ThrowsAsync<ZooKeeperProtocolException>(() => add).ConfigureAwait(true);
        Assert.Equal(1, Client.Manager.WatchCount);

        var barrier = Client.SyncAsync("/barrier", token);
        var sync = await SessionTestSupport.NextAsync(_peer).ConfigureAwait(true);
        Assert.Equal((int)ZooKeeperOperation.Sync, sync.Opcode);
        Notify(_peer, ZooKeeperWatchEventKind.NodeDataChanged, "/app" + eventPath);
        _peer.Reply(sync);
        await barrier.ConfigureAwait(true);
        await using var reader = existing.ReadEventsAsync(token).GetAsyncEnumerator(token);
        Assert.True(await reader.MoveNextAsync().ConfigureAwait(true));
        Assert.Equal(existing.RegistrationId, reader.Current.RegistrationId);
        Assert.Equal(eventPath, reader.Current.Path);
        Assert.Equal(ZooKeeperWatchEventKind.NodeDataChanged, reader.Current.Kind);
        Assert.True(existing.Active);
        Assert.Equal(0, _peer.Requests.Count);
    }

    [Fact(Timeout = TestTimeoutMilliseconds)]
    public async Task FailedAddAndCanceledLateFailureDoNotInventAWatchOrCleanup()
    {
        var pending = Client.AddWatchAsync(
            "/node",
            PersistentWatchMode.Persistent,
            TestContext.Current.CancellationToken
        );
        _peer.Reply(
            await SessionTestSupport.NextAsync(_peer).ConfigureAwait(true),
            (int)ZooKeeperErrorCode.NoAuth
        );
        var error = await Assert
            .ThrowsAsync<ZooKeeperException>(() => pending)
            .ConfigureAwait(true);
        Assert.Equal(RequestOutcome.Rejected, error.Outcome);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken
        );
        pending = Client.AddWatchAsync(
            "/node",
            PersistentWatchMode.PersistentRecursive,
            stop.Token
        );
        var packet = await SessionTestSupport.NextAsync(_peer).ConfigureAwait(true);
        await stop.CancelAsync().ConfigureAwait(true);
        await Assert
            .ThrowsAsync<ZooKeeperOperationCanceledException>(() => pending)
            .ConfigureAwait(true);
        _peer.Reply(packet, (int)ZooKeeperErrorCode.Unimplemented);
        await BarrierAsync().ConfigureAwait(true);
        Assert.Equal(0, Client.Manager.WatchCount);
        Assert.Equal(0, _peer.Requests.Count);
    }

    [Fact(Timeout = TestTimeoutMilliseconds)]
    [Trait("Acceptance", "T26")]
    public async Task RestoreUsesExtendedVectorsAndPublishesGapOnlyAfterAcknowledgmentBeforeAdmission()
    {
        var exact = await RegisterAsync(PersistentWatchMode.Persistent).ConfigureAwait(true);
        var recursive = await RegisterAsync(PersistentWatchMode.PersistentRecursive)
            .ConfigureAwait(true);
        var missing = Client.ExistsAndWatchAsync("/missing", TestContext.Current.CancellationToken);
        _peer.Reply(
            await SessionTestSupport.NextAsync(_peer).ConfigureAwait(true),
            (int)ZooKeeperErrorCode.NoNode
        );
        var oneShot = (await missing.ConfigureAwait(true)).Watch;
        using var second = new SessionPeer { ReplyApplications = false, ReplyRestores = false };
        _fixture.Connector.Add(second);
        _peer.Drop();
        await SessionTestSupport.NextAsync(second).ConfigureAwait(true);
        var legacy = await SessionTestSupport.NextAsync(second).ConfigureAwait(true);
        Assert.Equal(new RestoreRequest(0, [], [], []).Opcode, legacy.Opcode);
        second.Reply(legacy);
        var reset = await SessionTestSupport.NextAsync(second).ConfigureAwait(true);
        Assert.Equal(new RestoreRequest(0, [], [], [], [], []).Opcode, reset.Opcode);
        var input = new JuteReader(reset.Bytes, ProtocolFixtures.Limits);
        input.ReadRequestHeader();
        Assert.Equal(Watermark, input.ReadInt64());
        Assert.Empty(input.ReadStringVector()!);
        Assert.Empty(input.ReadStringVector()!);
        Assert.Empty(input.ReadStringVector()!);
        Assert.Equal("/app/node", Assert.Single(input.ReadStringVector()!));
        Assert.Equal("/app/node", Assert.Single(input.ReadStringVector()!));
        input.EnsureEnd();
        Assert.Equal(ZooKeeperSessionState.Restoring, Client.Session.State);
        var readiness = Client.WaitForConnectionAsync(TestContext.Current.CancellationToken);
        Assert.False(readiness.IsCompleted);
        Notify(second, ZooKeeperWatchEventKind.NodeCreated, "/app/node/child");
        second.Reply(reset);
        await readiness.ConfigureAwait(true);
        var following = Client.SyncAsync("/barrier", TestContext.Current.CancellationToken);
        second.Reply(await SessionTestSupport.NextAsync(second).ConfigureAwait(true));
        await following.ConfigureAwait(true);
        Assert.Equal(1, exact.Queue.Reader.Count);
        Assert.Equal(2, recursive.Queue.Reader.Count);
        Assert.Equal(0, oneShot.Queue.Reader.Count);
        var reader = recursive
            .ReadEventsAsync(TestContext.Current.CancellationToken)
            .GetAsyncEnumerator(TestContext.Current.CancellationToken);
        await using var lifetime = reader.ConfigureAwait(true);
        Assert.True(await reader.MoveNextAsync().ConfigureAwait(true));
        Assert.Equal(ZooKeeperWatchEventKind.NodeCreated, reader.Current.Kind);
        Assert.True(await reader.MoveNextAsync().ConfigureAwait(true));
        Assert.Equal(ZooKeeperWatchEventKind.ReconnectedWithGap, reader.Current.Kind);
        Assert.Equal("/node", reader.Current.Path);
        Assert.Null(reader.Current.Zxid);
        Assert.Equal(Client.Session.ConnectionGeneration, reader.Current.ConnectionGeneration);
        Assert.Equal(Watermark, Client.Session.LastReplyZxid);
    }

    [Fact(Timeout = TestTimeoutMilliseconds)]
    public async Task DisposedCapturedRestoreIsCleanedWithoutGapAndUnknownAddIsNeverRestored()
    {
        var watch = await RegisterAsync(PersistentWatchMode.PersistentRecursive)
            .ConfigureAwait(true);
        var uncertain = Client.AddWatchAsync(
            "/uncertain",
            PersistentWatchMode.Persistent,
            TestContext.Current.CancellationToken
        );
        await SessionTestSupport.NextAsync(_peer).ConfigureAwait(true);
        using var second = new SessionPeer { ReplyApplications = false, ReplyRestores = false };
        _fixture.Connector.Add(second);
        _peer.Drop();
        var error = await Assert
            .ThrowsAsync<ZooKeeperException>(() => uncertain)
            .ConfigureAwait(true);
        Assert.Equal(RequestOutcome.Unknown, error.Outcome);
        await SessionTestSupport.NextAsync(second).ConfigureAwait(true);
        var reset = await SessionTestSupport.NextAsync(second).ConfigureAwait(true);
        var input = new JuteReader(reset.Bytes, ProtocolFixtures.Limits);
        input.ReadRequestHeader();
        input.ReadInt64();
        Assert.Empty(input.ReadStringVector()!);
        Assert.Empty(input.ReadStringVector()!);
        Assert.Empty(input.ReadStringVector()!);
        Assert.Empty(input.ReadStringVector()!);
        Assert.Equal("/app/node", Assert.Single(input.ReadStringVector()!));
        await watch.DisposeAsync().ConfigureAwait(true);
        second.Reply(reset);
        await Client
            .WaitForConnectionAsync(TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        var cleanup = await SessionTestSupport.NextAsync(second).ConfigureAwait(true);
        AssertWatchRequest(
            cleanup,
            ZooKeeperOperation.RemoveWatches,
            "/app/node",
            WatchProtocol.PersistentRecursive
        );
        second.Reply(cleanup);
        Assert.Equal(0, watch.Queue.Reader.Count);
        Assert.Equal(0, Client.Manager.WatchCount);
    }

    [Fact(Timeout = TestTimeoutMilliseconds)]
    [Trait("Acceptance", "T30")]
    public async Task SlowPersistentConsumerFaultsTheClientWhenGlobalBudgetIsExhausted()
    {
        var fixture = new SessionFixture();
        await using var fixtureLifetime = fixture.ConfigureAwait(true);
        using var peer = new SessionPeer { ReplyApplications = true };
        var client = await fixture
            .ConnectAsync(
                peer,
                SessionFixture.Options(events: 1, connection: "first:2181,second:2181"),
                TestContext.Current.CancellationToken
            )
            .ConfigureAwait(true);
        await SessionTestSupport.NextAsync(peer).ConfigureAwait(true);
        var watch = await client
            .AddWatchAsync(
                "/node",
                PersistentWatchMode.Persistent,
                TestContext.Current.CancellationToken
            )
            .ConfigureAwait(true);
        await SessionTestSupport.NextAsync(peer).ConfigureAwait(true);
        Notify(peer, ZooKeeperWatchEventKind.NodeCreated, "/node");
        Notify(peer, ZooKeeperWatchEventKind.NodeDataChanged, "/node");
        await SessionTestSupport.Bounded(client.Manager.Completion).ConfigureAwait(true);
        Assert.Equal(ZooKeeperSessionState.Faulted, client.Session.State);
        var reader = watch
            .ReadEventsAsync(TestContext.Current.CancellationToken)
            .GetAsyncEnumerator(TestContext.Current.CancellationToken);
        await using var lifetime = reader.ConfigureAwait(true);
        Assert.True(await reader.MoveNextAsync().ConfigureAwait(true));
        await Assert
            .ThrowsAsync<ZooKeeperEventOverflowException>(() => reader.MoveNextAsync().AsTask())
            .ConfigureAwait(true);
    }

    [Fact(Timeout = TestTimeoutMilliseconds)]
    public async Task GapPublicationOverflowRevokesReadinessAndCompletesStreamsWithFault()
    {
        var fixture = new SessionFixture();
        await using var fixtureLifetime = fixture.ConfigureAwait(true);
        using var peer = new SessionPeer { ReplyApplications = true };
        var client = await fixture
            .ConnectAsync(
                peer,
                SessionFixture.Options(events: 1, connection: "first:2181,second:2181"),
                TestContext.Current.CancellationToken
            )
            .ConfigureAwait(true);
        await SessionTestSupport.NextAsync(peer).ConfigureAwait(true);
        var watch = await client
            .AddWatchAsync(
                "/node",
                PersistentWatchMode.Persistent,
                TestContext.Current.CancellationToken
            )
            .ConfigureAwait(true);
        await SessionTestSupport.NextAsync(peer).ConfigureAwait(true);
        Notify(peer, ZooKeeperWatchEventKind.NodeCreated, "/node");
        // Ordered reply ensures the first event occupies the budget before transport loss.
        await client
            .SyncAsync("/barrier", TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        await SessionTestSupport.NextAsync(peer).ConfigureAwait(true);
        using var second = new SessionPeer();
        fixture.Connector.Add(second);
        peer.Drop();
        await SessionTestSupport.Bounded(client.Manager.Completion).ConfigureAwait(true);
        Assert.Equal(ZooKeeperSessionState.Faulted, client.Session.State);
        await Assert
            .ThrowsAsync<ZooKeeperEventOverflowException>(() =>
                client.WaitForConnectionAsync(TestContext.Current.CancellationToken)
            )
            .ConfigureAwait(true);
        Assert.Equal(1, watch.Queue.Reader.Count);
    }

    [Theory(Timeout = TestTimeoutMilliseconds)]
    [InlineData(PersistentWatchMode.Persistent)]
    [InlineData(PersistentWatchMode.PersistentRecursive)]
    public async Task PersistentRemovalNotificationEndsOnlyExactPathPersistentStreams(
        PersistentWatchMode mode
    )
    {
        var watch = await RegisterAsync(mode).ConfigureAwait(true);
        var ancestor = await RegisterAsync(PersistentWatchMode.PersistentRecursive, "/")
            .ConfigureAwait(true);
        Notify(_peer, ZooKeeperWatchEventKind.PersistentWatchRemoved, "/app/node");
        await BarrierAsync().ConfigureAwait(true);
        Assert.Equal(0, ancestor.Queue.Reader.Count);
        var reader = watch
            .ReadEventsAsync(TestContext.Current.CancellationToken)
            .GetAsyncEnumerator(TestContext.Current.CancellationToken);
        await using var lifetime = reader.ConfigureAwait(true);
        Assert.True(await reader.MoveNextAsync().ConfigureAwait(true));
        Assert.Equal(ZooKeeperWatchEventKind.PersistentWatchRemoved, reader.Current.Kind);
        Assert.False(await reader.MoveNextAsync().ConfigureAwait(true));
    }

    [Fact(Timeout = TestTimeoutMilliseconds)]
    public async Task PreCanceledAddIsNotSentAndInvalidModeDoesNotConsumeCapacity()
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken
        );
        await stop.CancelAsync().ConfigureAwait(true);
        var error = await Assert
            .ThrowsAsync<ZooKeeperOperationCanceledException>(() =>
                Client.AddWatchAsync("/node", PersistentWatchMode.Persistent, stop.Token)
            )
            .ConfigureAwait(true);
        Assert.Equal(RequestOutcome.NotSent, error.Outcome);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
        {
            _ = Client.AddWatchAsync(
                "/node",
                (PersistentWatchMode)(-1),
                TestContext.Current.CancellationToken
            );
        });
        Assert.Throws<ArgumentException>(() =>
        {
            _ = Client.AddWatchAsync(
                "relative",
                PersistentWatchMode.Persistent,
                TestContext.Current.CancellationToken
            );
        });
        Assert.Equal(0, Client.Manager.WatchCount);
        Assert.Equal(0, _peer.Requests.Count);
    }

    [Fact(Timeout = StressTimeoutMilliseconds)]
    [Trait("Acceptance", "T27")]
    public async Task TenThousandPersistentUnicodePathsRestoreInBoundedExtendedBatchesWithOneWatermark()
    {
        const int watchCount = 10_000;
        _peer.ReplyApplications = true;
        string suffix = new('界', 20);
        for (int index = 0; index < watchCount; index++)
        {
            await Client
                .AddWatchAsync(
                    "/w-" + index.ToString(CultureInfo.InvariantCulture) + suffix,
                    index % 2 == 0
                        ? PersistentWatchMode.Persistent
                        : PersistentWatchMode.PersistentRecursive,
                    TestContext.Current.CancellationToken
                )
                .ConfigureAwait(true);
            await SessionTestSupport.NextAsync(_peer).ConfigureAwait(true);
            while (_peer.DrainReadWaits()) { }
        }
        using var second = new SessionPeer { ReplyApplications = true };
        _fixture.Connector.Add(second);
        _peer.Drop();
        await SessionTestSupport.NextAsync(second).ConfigureAwait(true);
        var restored = new HashSet<string>(StringComparer.Ordinal);
        int frames = 0;
        while (restored.Count < watchCount)
        {
            var packet = await SessionTestSupport.NextAsync(second).ConfigureAwait(true);
            Assert.Equal(new RestoreRequest(0, [], [], [], [], []).Opcode, packet.Opcode);
            Assert.True(packet.Bytes.Length <= WatchProtocol.RestoreTargetBytes);
            var input = new JuteReader(packet.Bytes, ProtocolFixtures.Limits);
            input.ReadRequestHeader();
            Assert.Equal(Watermark, input.ReadInt64());
            Assert.Empty(input.ReadStringVector()!);
            Assert.Empty(input.ReadStringVector()!);
            Assert.Empty(input.ReadStringVector()!);
            foreach (var path in input.ReadStringVector()!.Concat(input.ReadStringVector()!))
                Assert.True(restored.Add(path!));
            input.EnsureEnd();
            frames++;
        }
        Assert.True(frames > 1);
        await Client
            .WaitForConnectionAsync(TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        Assert.Equal(ZooKeeperSessionState.Connected, Client.Session.State);
        Assert.Equal(watchCount, Client.Manager.WatchCount);
    }

    [Theory(Timeout = TestTimeoutMilliseconds)]
    [InlineData(PersistentWatchMode.Persistent)]
    [InlineData(PersistentWatchMode.PersistentRecursive)]
    public async Task CommonProfileRejectsConflictingModesUntilAnyRemovalIsAcknowledged(
        PersistentWatchMode mode
    )
    {
        var fixture = new SessionFixture();
        await using var fixtureLifetime = fixture.ConfigureAwait(true);
        using var peer = new SessionPeer { ReplyApplications = false };
        var client = await fixture
            .ConnectAsync(peer, SessionFixture.Options(), TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        await SessionTestSupport.NextAsync(peer).ConfigureAwait(true);
        var add = client.AddWatchAsync("/node", mode, TestContext.Current.CancellationToken);
        peer.Reply(await SessionTestSupport.NextAsync(peer).ConfigureAwait(true));
        var watch = await add.ConfigureAwait(true);
        var otherMode =
            mode == PersistentWatchMode.Persistent
                ? PersistentWatchMode.PersistentRecursive
                : PersistentWatchMode.Persistent;
        Assert.Throws<NotSupportedException>(() =>
        {
            _ = client.AddWatchAsync("/node", otherMode, TestContext.Current.CancellationToken);
        });
        Assert.Throws<NotSupportedException>(() =>
        {
            _ = client.GetChildrenAndWatchAsync("/node", TestContext.Current.CancellationToken);
        });
        Assert.Throws<NotSupportedException>(() =>
        {
            _ = client.GetDataAndWatchAsync("/node", TestContext.Current.CancellationToken);
        });
        await watch.DisposeAsync().ConfigureAwait(true);
        var cleanup = await SessionTestSupport.NextAsync(peer).ConfigureAwait(true);
        AssertWatchRequest(cleanup, ZooKeeperOperation.RemoveWatches, "/node", WatchProtocol.Any);
        Assert.Throws<ZooKeeperBackpressureException>(() =>
        {
            _ = client.AddWatchAsync("/node", otherMode, TestContext.Current.CancellationToken);
        });
        peer.Reply(cleanup);
        var barrier = client.SyncAsync("/barrier", TestContext.Current.CancellationToken);
        peer.Reply(await SessionTestSupport.NextAsync(peer).ConfigureAwait(true));
        await barrier.ConfigureAwait(true);
        var read = client.ExistsAndWatchAsync("/node", TestContext.Current.CancellationToken);
        peer.Reply(
            await SessionTestSupport.NextAsync(peer).ConfigureAwait(true),
            (int)ZooKeeperErrorCode.NoNode
        );
        await read.ConfigureAwait(true);
        Assert.Throws<NotSupportedException>(() =>
        {
            _ = client.AddWatchAsync("/node", otherMode, TestContext.Current.CancellationToken);
        });
    }

    [Theory(Timeout = TestTimeoutMilliseconds)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CanceledConfirmationRetainsItsBoundAndCannotLeaveAnOldModeBehind(bool sent)
    {
        var fixture = new SessionFixture();
        await using var fixtureLifetime = fixture.ConfigureAwait(true);
        using var peer = new SessionPeer { ReplyApplications = false };
        var client = await fixture
            .ConnectAsync(peer, SessionFixture.Options(), TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        await SessionTestSupport.NextAsync(peer).ConfigureAwait(true);
        var add = client.AddWatchAsync(
            "/node",
            PersistentWatchMode.Persistent,
            TestContext.Current.CancellationToken
        );
        peer.Reply(await SessionTestSupport.NextAsync(peer).ConfigureAwait(true));
        var watch = await add.ConfigureAwait(true);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken
        );
        if (!sent)
            await stop.CancelAsync().ConfigureAwait(true);
        var removal = watch.RemoveAsync(WatchRemovalMode.ServerConfirmed, stop.Token);
        var packet = await SessionTestSupport.NextAsync(peer).ConfigureAwait(true);
        if (sent)
            await stop.CancelAsync().ConfigureAwait(true);
        var failure = await Assert
            .ThrowsAsync<ZooKeeperOperationCanceledException>(() => removal)
            .ConfigureAwait(true);
        Assert.Equal(sent ? RequestOutcome.Unknown : RequestOutcome.NotSent, failure.Outcome);
        AssertWatchRequest(packet, ZooKeeperOperation.RemoveWatches, "/node", WatchProtocol.Any);
        Assert.Throws<ZooKeeperBackpressureException>(() =>
        {
            _ = client.AddWatchAsync(
                "/node",
                PersistentWatchMode.PersistentRecursive,
                TestContext.Current.CancellationToken
            );
        });
        peer.Reply(packet);
        var barrier = client.SyncAsync("/barrier", TestContext.Current.CancellationToken);
        peer.Reply(await SessionTestSupport.NextAsync(peer).ConfigureAwait(true));
        await barrier.ConfigureAwait(true);
        var next = client.AddWatchAsync(
            "/node",
            PersistentWatchMode.PersistentRecursive,
            TestContext.Current.CancellationToken
        );
        peer.Reply(await SessionTestSupport.NextAsync(peer).ConfigureAwait(true));
        await next.ConfigureAwait(true);
        Assert.Equal(1, client.Manager.WatchCount);
    }

    [Theory(Timeout = TestTimeoutMilliseconds)]
    [InlineData(WatchProtocol.Success, WatchRemovalStatus.ServerConfirmed)]
    [InlineData((int)ZooKeeperErrorCode.NoWatcher, WatchRemovalStatus.NoWatcher)]
    [Trait("Acceptance", "T29")]
    public async Task MalformedRemovalAcknowledgmentLeavesConfirmationRetryable(
        int retryCode,
        WatchRemovalStatus expected
    )
    {
        var token = TestContext.Current.CancellationToken;
        var watch = await RegisterAsync(PersistentWatchMode.Persistent).ConfigureAwait(true);
        var generation = Client.Session.ConnectionGeneration;
        var removal = watch.RemoveAsync(WatchRemovalMode.ServerConfirmed, token);
        var packet = await SessionTestSupport.NextAsync(_peer).ConfigureAwait(true);
        AssertWatchRequest(
            packet,
            ZooKeeperOperation.RemoveWatches,
            "/app/node",
            WatchProtocol.Persistent
        );
        var next = Client.SyncAsync("/next", token);
        _peer.Inject([.. SessionPeer.Packet(packet.Xid), 1]);

        var failure = await Assert
            .ThrowsAsync<ZooKeeperProtocolException>(() => removal)
            .ConfigureAwait(true);
        Assert.Equal(RequestOutcome.Unknown, failure.Outcome);
        Assert.Equal(packet.Xid, failure.Request.RequestId);
        Assert.Equal(ZooKeeperOperation.RemoveWatches, failure.Request.Operation);
        Assert.Null(failure.ErrorCode);
        Assert.Null(watch.ConfirmedRemoval);
        Assert.True(watch.Suppressed);
        Notify(_peer, ZooKeeperWatchEventKind.NodeDataChanged, "/app/node");
        var sync = await SessionTestSupport.NextAsync(_peer).ConfigureAwait(true);
        Assert.Equal((int)ZooKeeperOperation.Sync, sync.Opcode);
        _peer.Reply(sync);
        await next.ConfigureAwait(true);
        Assert.Equal(0, watch.Queue.Reader.Count);
        Assert.Equal(ZooKeeperSessionState.Connected, Client.Session.State);
        Assert.Equal(generation, Client.Session.ConnectionGeneration);

        var retry = watch.RemoveAsync(WatchRemovalMode.ServerConfirmed, token);
        var retryPacket = await SessionTestSupport.NextAsync(_peer).ConfigureAwait(true);
        AssertWatchRequest(
            retryPacket,
            ZooKeeperOperation.RemoveWatches,
            "/app/node",
            WatchProtocol.Persistent
        );
        _peer.Reply(retryPacket, retryCode);
        Assert.Equal(expected, (await retry.ConfigureAwait(true)).Status);
    }

    [Fact(Timeout = TestTimeoutMilliseconds)]
    [Trait("Acceptance", "T29")]
    public async Task MalformedCleanupAcknowledgmentRetainsCapacityAndProtectsTheNextOwner()
    {
        var token = TestContext.Current.CancellationToken;
        using var peer = new SessionPeer { ReplyApplications = false };
        var fixture = new SessionFixture();
        await using var fixtureLifetime = fixture.ConfigureAwait(true);
        var client = await fixture
            .ConnectAsync(peer, SessionFixture.Options(watches: 1), token)
            .ConfigureAwait(true);
        await SessionTestSupport.NextAsync(peer).ConfigureAwait(true);
        var add = client.AddWatchAsync("/node", PersistentWatchMode.Persistent, token);
        peer.Reply(await SessionTestSupport.NextAsync(peer).ConfigureAwait(true));
        var original = await add.ConfigureAwait(true);
        var generation = client.Session.ConnectionGeneration;
        const string counter = "zklink.protocol.rejected_replies";
        using var capture = new TelemetryCapture();
        await ZooKeeperTelemetry.FlushAsync(token).ConfigureAwait(true);
        capture.Collect();
        double before = capture.Value(counter);

        await original.DisposeAsync().ConfigureAwait(true);
        var cleanup = await SessionTestSupport.NextAsync(peer).ConfigureAwait(true);
        AssertWatchRequest(cleanup, ZooKeeperOperation.RemoveWatches, "/node", WatchProtocol.Any);
        peer.Inject([.. SessionPeer.Packet(cleanup.Xid), 1]);
        var barrier = client.SyncAsync("/next", token);
        var sync = await SessionTestSupport.NextAsync(peer).ConfigureAwait(true);
        Assert.Equal((int)ZooKeeperOperation.Sync, sync.Opcode);
        peer.Reply(sync);
        await barrier.ConfigureAwait(true);
        Assert.Equal(ZooKeeperSessionState.Connected, client.Session.State);
        Assert.Equal(generation, client.Session.ConnectionGeneration);
        capture.Collect();
        Assert.Equal(before + 1, capture.Value(counter));
        Assert.Throws<ZooKeeperBackpressureException>(() =>
        {
            _ = client.AddWatchAsync("/other", PersistentWatchMode.Persistent, token);
        });

        var replacement = client.AddWatchAsync("/node", PersistentWatchMode.Persistent, token);
        var packet = await SessionTestSupport.NextAsync(peer).ConfigureAwait(true);
        AssertWatchRequest(
            packet,
            ZooKeeperOperation.AddWatch,
            "/node",
            (int)PersistentWatchMode.Persistent
        );
        peer.Reply(packet);
        var survivor = await replacement.ConfigureAwait(true);
        var status = await original
            .RemoveAsync(WatchRemovalMode.ServerConfirmed, token)
            .ConfigureAwait(true);
        Assert.Equal(WatchRemovalStatus.SharedRegistrationRetained, status.Status);
        Notify(peer, ZooKeeperWatchEventKind.NodeDataChanged, "/node");
        await using var events = survivor.ReadEventsAsync(token).GetAsyncEnumerator(token);
        Assert.True(await events.MoveNextAsync().ConfigureAwait(true));
        Assert.Equal(survivor.RegistrationId, events.Current.RegistrationId);
        Assert.Equal(ZooKeeperWatchEventKind.NodeDataChanged, events.Current.Kind);
        Assert.Equal(0, peer.Requests.Count);
    }

    [Fact(Timeout = TestTimeoutMilliseconds)]
    public async Task RejectedRemovalRetainsCapacityWithoutSpinningAndAnAcknowledgedRetryReleasesIt()
    {
        var fixture = new SessionFixture();
        await using var fixtureLifetime = fixture.ConfigureAwait(true);
        using var peer = new SessionPeer { ReplyApplications = false };
        var client = await fixture
            .ConnectAsync(
                peer,
                SessionFixture.Options(watches: 1),
                TestContext.Current.CancellationToken
            )
            .ConfigureAwait(true);
        await SessionTestSupport.NextAsync(peer).ConfigureAwait(true);
        var add = client.AddWatchAsync(
            "/node",
            PersistentWatchMode.Persistent,
            TestContext.Current.CancellationToken
        );
        peer.Reply(await SessionTestSupport.NextAsync(peer).ConfigureAwait(true));
        var watch = await add.ConfigureAwait(true);
        var removal = watch.RemoveAsync(
            WatchRemovalMode.ServerConfirmed,
            TestContext.Current.CancellationToken
        );
        peer.Reply(
            await SessionTestSupport.NextAsync(peer).ConfigureAwait(true),
            (int)ZooKeeperErrorCode.NoAuth
        );
        await Assert.ThrowsAsync<ZooKeeperException>(() => removal).ConfigureAwait(true);
        var barrier = client.SyncAsync("/barrier", TestContext.Current.CancellationToken);
        var barrierPacket = await SessionTestSupport.NextAsync(peer).ConfigureAwait(true);
        Assert.Equal((int)ZooKeeperOperation.Sync, barrierPacket.Opcode);
        peer.Reply(barrierPacket);
        await barrier.ConfigureAwait(true);
        Assert.Equal(0, peer.Requests.Count);
        Assert.Throws<ZooKeeperBackpressureException>(() =>
        {
            _ = client.AddWatchAsync(
                "/other",
                PersistentWatchMode.PersistentRecursive,
                TestContext.Current.CancellationToken
            );
        });
        var retry = watch.RemoveAsync(
            WatchRemovalMode.ServerConfirmed,
            TestContext.Current.CancellationToken
        );
        peer.Reply(await SessionTestSupport.NextAsync(peer).ConfigureAwait(true));
        Assert.Equal(WatchRemovalStatus.ServerConfirmed, (await retry.ConfigureAwait(true)).Status);
        var next = client.AddWatchAsync(
            "/other",
            PersistentWatchMode.PersistentRecursive,
            TestContext.Current.CancellationToken
        );
        peer.Reply(await SessionTestSupport.NextAsync(peer).ConfigureAwait(true));
        await next.ConfigureAwait(true);
    }

    private async Task<ZooKeeperWatch> RegisterAsync(
        PersistentWatchMode mode,
        string path = "/node"
    )
    {
        var task = Client.AddWatchAsync(path, mode, TestContext.Current.CancellationToken);
        _peer.Reply(await SessionTestSupport.NextAsync(_peer).ConfigureAwait(false));
        return await task.ConfigureAwait(false);
    }

    private async Task BarrierAsync()
    {
        var task = Client.SyncAsync("/barrier", TestContext.Current.CancellationToken);
        _peer.Reply(await SessionTestSupport.NextAsync(_peer).ConfigureAwait(false));
        await task.ConfigureAwait(false);
    }

    private static void AssertWatchRequest(
        ReceivedPacket packet,
        ZooKeeperOperation operation,
        string path,
        int mode
    )
    {
        var input = new JuteReader(packet.Bytes, ProtocolFixtures.Limits);
        Assert.Equal((int)operation, input.ReadRequestHeader().Opcode);
        Assert.Equal(path, input.ReadString());
        Assert.Equal(mode, input.ReadInt32());
        input.EnsureEnd();
    }

    private static void Notify(SessionPeer peer, ZooKeeperWatchEventKind kind, string path)
    {
        var bytes = new byte[PacketBytes];
        var output = new JuteWriter(bytes, ProtocolFixtures.Limits);
        output.WriteReplyHeader(
            new ReplyHeader(WatchProtocol.NotificationXid, Watermark, WatchProtocol.Success)
        );
        output.WriteInt32((int)kind);
        output.WriteInt32(WatchProtocol.ServerConnected);
        output.WriteString(path);
        peer.Inject(bytes[..output.WrittenCount]);
    }
}
