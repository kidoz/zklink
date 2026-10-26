using ZkLink.Protocol;
using ZkLink.Sessions;
using ZkLink.Watches;

namespace ZkLink.Protocol.Tests.Sessions;

public sealed class WatchRecoveryTests : IAsyncLifetime
{
    private const int TestTimeoutMilliseconds = 10_000;
    private const int NegotiatedMilliseconds = 3000;
    private const int PacketBytes = 256;
    private const int ServerReadOnly = 5;
    private const long Watermark = 12;
    private const long StaleZxid = 999;
    private readonly SessionFixture _fixture = new();
    private readonly List<SessionPeer> _peers = [];
    private SessionPeer First => _peers[0];
    private static readonly string[] RemainingPaths = ["/first", "/last"];
    private ZooKeeperClient Client => _fixture.Client!;

    private SessionPeer Peer(bool restores = false)
    {
        var peer = new SessionPeer
        {
            NegotiatedTimeout = NegotiatedMilliseconds,
            ReplyApplications = false,
            ReplyRestores = restores,
        };
        _peers.Add(peer);
        return peer;
    }

    public async ValueTask InitializeAsync()
    {
        Peer();
        await _fixture
            .ConnectAsync(
                First,
                SessionFixture.Options(
                    collections: 2,
                    connection: "first:2181,second:2181,third:2181,fourth:2181"
                ),
                TestContext.Current.CancellationToken
            )
            .ConfigureAwait(true);
        await SessionTestSupport.NextAsync(First).ConfigureAwait(true);
    }

    public async ValueTask DisposeAsync()
    {
        await _fixture.DisposeAsync().ConfigureAwait(true);
        foreach (var peer in _peers)
            peer.Dispose();
    }

    [Theory(Timeout = TestTimeoutMilliseconds)]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Acceptance", "T26")]
    [Trait("Acceptance", "T27")]
    [Trait("Acceptance", "T29")]
    public async Task InterruptedMultiBatchRestoreRetriesWithoutAdmissionReplayOrRevivingDisposedHandles(
        bool timeout
    )
    {
        var watches = new[]
        {
            await RegisterAsync("/first").ConfigureAwait(true),
            await RegisterAsync("/disposed").ConfigureAwait(true),
            await RegisterAsync("/last").ConfigureAwait(true),
        };
        var uncertain = Client.SyncAsync("/uncertain", TestContext.Current.CancellationToken);
        await SessionTestSupport.NextAsync(First).ConfigureAwait(true);
        var second = Peer();
        _fixture.Connector.Add(second);
        First.Drop();
        await NextDuringRecoveryAsync(second).ConfigureAwait(true);
        var failure = await Assert
            .ThrowsAsync<ZooKeeperException>(() => uncertain)
            .ConfigureAwait(true);
        Assert.Equal(RequestOutcome.Unknown, failure.Outcome);
        var firstBatch = await NextDuringRecoveryAsync(second).ConfigureAwait(true);
        Assert.Equal(2, RestorePaths(firstBatch).Length);
        second.Reply(firstBatch, zxid: StaleZxid);
        var lastBatch = await NextDuringRecoveryAsync(second).ConfigureAwait(true);
        Assert.Single(RestorePaths(lastBatch));
        long failedGeneration = Client.Session.ConnectionGeneration;
        await AssertAdmissionClosedAsync(second).ConfigureAwait(true);
        var readiness = Client.WaitForConnectionAsync(TestContext.Current.CancellationToken);
        Assert.False(readiness.IsCompleted);
        await watches[1].DisposeAsync().ConfigureAwait(true);
        var third = Peer();
        _fixture.Connector.Add(third);
        if (timeout)
        {
            await _fixture
                .Time.WaitForTimerAsync(
                    SessionFixture.Options().ConnectAttemptTimeout,
                    TestContext.Current.CancellationToken
                )
                .ConfigureAwait(true);
            _fixture.Time.Advance(SessionFixture.Options().ConnectAttemptTimeout);
        }
        else
            second.Drop();
        var handshake = await NextDuringRecoveryAsync(third).ConfigureAwait(true);
        AssertResume(handshake);
        Assert.True(second.IsDisposed);
        var reset = await NextDuringRecoveryAsync(third).ConfigureAwait(true);
        Assert.Equal(RemainingPaths, RestorePaths(reset));
        await AssertAdmissionClosedAsync(third).ConfigureAwait(true);
        Assert.False(readiness.IsCompleted);
        Assert.All(watches, watch => Assert.Equal(0, watch.Queue.Reader.Count));
        third.Reply(reset);
        await readiness.ConfigureAwait(true);
        Assert.Equal(3, Client.Session.ConnectionGeneration);
        Assert.Equal(2, Client.Manager.WatchCount);
        Assert.Equal(Watermark, Client.Session.LastReplyZxid);
        Assert.Equal(ZooKeeperSessionState.Connected, Client.Session.State);
        Assert.Equal(1, watches[0].Queue.Reader.Count);
        Assert.Equal(1, watches[2].Queue.Reader.Count);
        var work = Client.SyncAsync("/new", TestContext.Current.CancellationToken);
        var packet = await SessionTestSupport.NextAsync(third).ConfigureAwait(true);
        var pending = Client.Manager.Scheduler.Expected(
            Client.Session.ConnectionGeneration,
            packet.Xid
        )!;
        Assert.Equal(
            RuntimeStop.Stopped,
            Client
                .Manager.Reply(
                    failedGeneration,
                    pending,
                    new WireReply(
                        new ReplyHeader(packet.Xid, StaleZxid, WatchProtocol.Success),
                        new EmptyResult()
                    )
                )!
                .Value.Kind
        );
        Client.Manager.Inbound(
            failedGeneration,
            new ReplyHeader(packet.Xid, StaleZxid, WatchProtocol.Success)
        );
        Assert.Equal(
            RuntimeStop.Stopped,
            Client
                .Manager.Notification(
                    failedGeneration,
                    new WireNotification(
                        (int)ZooKeeperWatchEventKind.NodeDataChanged,
                        WatchProtocol.ServerConnected,
                        "/first",
                        StaleZxid
                    )
                )!
                .Value.Kind
        );
        Assert.False(work.IsCompleted);
        Assert.Equal(Watermark, Client.Session.LastReplyZxid);
        Assert.Equal(1, watches[0].Queue.Reader.Count);
        third.Reply(packet);
        await work.ConfigureAwait(true);
        var reader = watches[0]
            .ReadEventsAsync(TestContext.Current.CancellationToken)
            .GetAsyncEnumerator(TestContext.Current.CancellationToken);
        await using var lifetime = reader.ConfigureAwait(true);
        Assert.True(await reader.MoveNextAsync().ConfigureAwait(true));
        Assert.Equal(ZooKeeperWatchEventKind.ReconnectedWithGap, reader.Current.Kind);
        Assert.Equal(Client.Session.ConnectionGeneration, reader.Current.ConnectionGeneration);
        Assert.Null(reader.Current.Zxid);
    }

    [Theory(Timeout = TestTimeoutMilliseconds)]
    [InlineData(ZooKeeperErrorCode.ConnectionLoss)]
    [InlineData(ZooKeeperErrorCode.OperationTimeout)]
    [InlineData(ZooKeeperErrorCode.UnknownSession)]
    [InlineData(ZooKeeperErrorCode.SessionMoved)]
    [InlineData(ZooKeeperErrorCode.RequestTimeout)]
    [Trait("Acceptance", "T26")]
    public async Task TransientRestoreRejectionRetriesTheSameSessionWithoutPublishingAnEarlyGap(
        ZooKeeperErrorCode code
    )
    {
        var watch = await RegisterAsync("/node").ConfigureAwait(true);
        var second = Peer();
        _fixture.Connector.Add(second);
        First.Drop();
        await NextDuringRecoveryAsync(second).ConfigureAwait(true);
        var reset = await NextDuringRecoveryAsync(second).ConfigureAwait(true);
        await AssertAdmissionClosedAsync(second).ConfigureAwait(true);
        var third = Peer();
        _fixture.Connector.Add(third);
        second.Reply(reset, (int)code);
        AssertResume(await NextDuringRecoveryAsync(third).ConfigureAwait(true));
        reset = await NextDuringRecoveryAsync(third).ConfigureAwait(true);
        Assert.Equal("/node", Assert.Single(RestorePaths(reset)));
        Assert.Equal(0, watch.Queue.Reader.Count);
        third.Reply(reset);
        await Client
            .WaitForConnectionAsync(TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        Assert.Equal(1, watch.Queue.Reader.Count);
        Assert.Equal(3, Client.Session.ConnectionGeneration);
    }

    [Theory(Timeout = TestTimeoutMilliseconds)]
    [InlineData(false, ZooKeeperErrorCode.SessionExpired, ZooKeeperSessionState.Expired)]
    [InlineData(true, ZooKeeperErrorCode.SessionExpired, ZooKeeperSessionState.Expired)]
    [InlineData(false, ZooKeeperErrorCode.AuthFailed, ZooKeeperSessionState.AuthFailed)]
    [InlineData(
        false,
        ZooKeeperErrorCode.SessionClosedRequiresSaslAuth,
        ZooKeeperSessionState.AuthFailed
    )]
    [InlineData(true, ZooKeeperErrorCode.AuthFailed, ZooKeeperSessionState.AuthFailed)]
    [Trait("Acceptance", "T10")]
    public async Task TerminalRestoreFailuresCompleteWatchStreamsAndReadinessWithoutReplacement(
        bool notification,
        ZooKeeperErrorCode code,
        ZooKeeperSessionState state
    )
    {
        var watch = await RegisterAsync("/node").ConfigureAwait(true);
        var second = Peer();
        _fixture.Connector.Add(second);
        First.Drop();
        await NextDuringRecoveryAsync(second).ConfigureAwait(true);
        var reset = await NextDuringRecoveryAsync(second).ConfigureAwait(true);
        var third = Peer();
        _fixture.Connector.Add(third);
        var readiness = Client.WaitForConnectionAsync(TestContext.Current.CancellationToken);
        if (notification)
            SessionEvent(
                second,
                state == ZooKeeperSessionState.Expired
                    ? (int)ZooKeeperErrorCode.SessionExpired
                    : WatchProtocol.ServerAuthFailed
            );
        else
            second.Reply(reset, (int)code);
        await AssertTerminalWithoutRetryAsync(third).ConfigureAwait(true);
        Assert.Equal(state, Client.Session.State);
        var error = await Assert
            .ThrowsAsync<ZooKeeperSessionException>(() => readiness)
            .ConfigureAwait(true);
        Assert.Equal(state, error.State);
        Assert.Equal((int)code, error.ErrorCode);
        var reader = watch
            .ReadEventsAsync(TestContext.Current.CancellationToken)
            .GetAsyncEnumerator(TestContext.Current.CancellationToken);
        await using var lifetime = reader.ConfigureAwait(true);
        var streamError = await Assert
            .ThrowsAsync<ZooKeeperSessionException>(() => reader.MoveNextAsync().AsTask())
            .ConfigureAwait(true);
        Assert.Equal(state, streamError.State);
        Assert.Equal(0, Client.Manager.Scheduler.PendingCount);
        Assert.Equal(0, Client.Manager.Scheduler.OwnedBytes);
        Assert.Equal(0, watch.Queue.Reader.Count);
        Assert.Equal(2, Client.Session.ConnectionGeneration);
        Assert.True(second.IsDisposed);
    }

    [Theory(Timeout = TestTimeoutMilliseconds)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FatalOrUnsupportedRestoreReplyFaultsWithoutReadinessOrGap(
        bool readOnlyNotification
    )
    {
        var watch = await RegisterAsync("/node").ConfigureAwait(true);
        var second = Peer();
        _fixture.Connector.Add(second);
        First.Drop();
        await NextDuringRecoveryAsync(second).ConfigureAwait(true);
        var reset = await NextDuringRecoveryAsync(second).ConfigureAwait(true);
        var third = Peer();
        _fixture.Connector.Add(third);
        if (readOnlyNotification)
            SessionEvent(second, ServerReadOnly);
        else
            second.Inject(SessionPeer.Packet(reset.Xid + 1));
        await AssertTerminalWithoutRetryAsync(third).ConfigureAwait(true);
        Assert.Equal(ZooKeeperSessionState.Faulted, Client.Session.State);
        var error = await Assert
            .ThrowsAsync<ZooKeeperConnectionException>(() =>
                Client.WaitForConnectionAsync(TestContext.Current.CancellationToken)
            )
            .ConfigureAwait(true);
        Assert.Equal(ZooKeeperConnectionFailure.Protocol, error.Failure);
        Assert.Equal(0, watch.Queue.Reader.Count);
    }

    [Fact(Timeout = TestTimeoutMilliseconds)]
    [Trait("Acceptance", "T09")]
    public async Task SilenceDuringRestorationEndsInLocalLostAndNeverReplacesTheSession()
    {
        var watch = await RegisterAsync("/node").ConfigureAwait(true);
        var second = Peer();
        _fixture.Connector.Add(second);
        First.Drop();
        await NextDuringRecoveryAsync(second).ConfigureAwait(true);
        await NextDuringRecoveryAsync(second).ConfigureAwait(true);
        var readiness = Client.WaitForConnectionAsync(TestContext.Current.CancellationToken);
        _fixture.Time.Advance(TimeSpan.FromMilliseconds(NegotiatedMilliseconds));
        await SessionTestSupport.Bounded(Client.Manager.Completion).ConfigureAwait(true);
        Assert.Equal(ZooKeeperSessionState.Lost, Client.Session.State);
        Assert.Equal(2, Client.Session.ConnectionGeneration);
        var error = await Assert
            .ThrowsAsync<ZooKeeperSessionException>(() => readiness)
            .ConfigureAwait(true);
        Assert.Equal(ZooKeeperSessionState.Lost, error.State);
        Assert.Null(error.ErrorCode);
        var reader = watch
            .ReadEventsAsync(TestContext.Current.CancellationToken)
            .GetAsyncEnumerator(TestContext.Current.CancellationToken);
        await using var lifetime = reader.ConfigureAwait(true);
        await Assert
            .ThrowsAsync<ZooKeeperSessionException>(() => reader.MoveNextAsync().AsTask())
            .ConfigureAwait(true);
        Assert.Equal(0, Client.Manager.Scheduler.PendingCount);
        Assert.Equal(0, watch.Queue.Reader.Count);
    }

    [Fact(Timeout = TestTimeoutMilliseconds)]
    [Trait("Acceptance", "T10")]
    public async Task ActiveExpiryTerminatesEveryWatchAndReportsSentAndQueuedWorkHonestly()
    {
        var watch = await RegisterAsync("/persistent").ConfigureAwait(true);
        var read = Client.ExistsAndWatchAsync("/missing", TestContext.Current.CancellationToken);
        First.Reply(
            await SessionTestSupport.NextAsync(First).ConfigureAwait(true),
            (int)ZooKeeperErrorCode.NoNode
        );
        var oneShot = (await read.ConfigureAwait(true)).Watch;
        First.HoldApplicationWrite = true;
        var sent = Client.SyncAsync("/sent", TestContext.Current.CancellationToken);
        await SessionTestSupport.NextAsync(First).ConfigureAwait(true);
        await SessionTestSupport.Bounded(First.ApplicationWriteHeld).ConfigureAwait(true);
        var queued = Client.SyncAsync("/queued", TestContext.Current.CancellationToken);
        SessionEvent(First, (int)ZooKeeperErrorCode.SessionExpired);
        await SessionTestSupport.Bounded(Client.Manager.Completion).ConfigureAwait(true);
        Assert.Equal(
            RequestOutcome.Unknown,
            (await Assert.ThrowsAsync<ZooKeeperException>(() => sent).ConfigureAwait(true)).Outcome
        );
        Assert.Equal(
            RequestOutcome.NotSent,
            (
                await Assert.ThrowsAsync<ZooKeeperException>(() => queued).ConfigureAwait(true)
            ).Outcome
        );
        foreach (var handle in new[] { watch, oneShot })
        {
            var reader = handle
                .ReadEventsAsync(TestContext.Current.CancellationToken)
                .GetAsyncEnumerator(TestContext.Current.CancellationToken);
            await using var lifetime = reader.ConfigureAwait(true);
            var error = await Assert
                .ThrowsAsync<ZooKeeperSessionException>(() => reader.MoveNextAsync().AsTask())
                .ConfigureAwait(true);
            Assert.Equal(ZooKeeperSessionState.Expired, error.State);
        }
        Assert.Equal(0, Client.Manager.Scheduler.PendingCount);
        Assert.Equal(0, Client.Manager.Scheduler.OwnedBytes);
        Assert.Equal(0, First.Requests.Count);
        Assert.Equal(1, Client.Session.ConnectionGeneration);
        await Client.CloseAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
        Assert.Equal(ZooKeeperCloseStatus.SessionExpired, Client.Session.CloseStatus);
    }

    [Fact(Timeout = TestTimeoutMilliseconds)]
    [Trait("Acceptance", "T26")]
    public async Task OneShotConsumedDuringAnAbortedRestoreIsNotRestoredAgain()
    {
        var persistent = await RegisterAsync("/persistent").ConfigureAwait(true);
        var read = Client.ExistsAndWatchAsync("/missing", TestContext.Current.CancellationToken);
        First.Reply(
            await SessionTestSupport.NextAsync(First).ConfigureAwait(true),
            (int)ZooKeeperErrorCode.NoNode
        );
        var oneShot = (await read.ConfigureAwait(true)).Watch;
        var second = Peer();
        _fixture.Connector.Add(second);
        First.Drop();
        await NextDuringRecoveryAsync(second).ConfigureAwait(true);
        var legacy = await NextDuringRecoveryAsync(second).ConfigureAwait(true);
        Assert.Equal(new RestoreRequest(0, [], [], []).Opcode, legacy.Opcode);
        var bytes = new byte[PacketBytes];
        var output = new JuteWriter(bytes, ProtocolFixtures.Limits);
        output.WriteReplyHeader(
            new ReplyHeader(WatchProtocol.NotificationXid, StaleZxid, WatchProtocol.Success)
        );
        output.WriteInt32((int)ZooKeeperWatchEventKind.NodeCreated);
        output.WriteInt32(WatchProtocol.ServerConnected);
        output.WriteString("/missing");
        second.Inject(bytes[..output.WrittenCount]);
        second.Reply(legacy);
        await NextDuringRecoveryAsync(second).ConfigureAwait(true);
        Assert.Equal(1, oneShot.Queue.Reader.Count);
        var third = Peer();
        _fixture.Connector.Add(third);
        second.Drop();
        AssertResume(await NextDuringRecoveryAsync(third).ConfigureAwait(true));
        var reset = await NextDuringRecoveryAsync(third).ConfigureAwait(true);
        Assert.Equal("/persistent", Assert.Single(RestorePaths(reset)));
        third.Reply(reset);
        await Client
            .WaitForConnectionAsync(TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        Assert.Equal(1, persistent.Queue.Reader.Count);
        var events = oneShot
            .ReadEventsAsync(TestContext.Current.CancellationToken)
            .GetAsyncEnumerator(TestContext.Current.CancellationToken);
        await using var lifetime = events.ConfigureAwait(true);
        Assert.True(await events.MoveNextAsync().ConfigureAwait(true));
        Assert.Equal(ZooKeeperWatchEventKind.NodeCreated, events.Current.Kind);
        Assert.Equal(2, events.Current.ConnectionGeneration);
        Assert.False(await events.MoveNextAsync().ConfigureAwait(true));
    }

    private async Task<ZooKeeperWatch> RegisterAsync(string path)
    {
        var pending = Client.AddWatchAsync(
            path,
            PersistentWatchMode.Persistent,
            TestContext.Current.CancellationToken
        );
        First.Reply(await SessionTestSupport.NextAsync(First).ConfigureAwait(false));
        return await pending.ConfigureAwait(false);
    }

    private async Task<ReceivedPacket> NextDuringRecoveryAsync(SessionPeer peer)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken
        );
        var next = peer.Requests.ReadAsync(stop.Token).AsTask();
        try
        {
            var winner = await Task.WhenAny(next, Client.Manager.Completion)
                .WaitAsync(SessionTestSupport.Guard, stop.Token)
                .ConfigureAwait(false);
            Assert.Same(next, winner);
            return await next.ConfigureAwait(false);
        }
        finally
        {
            await stop.CancelAsync().ConfigureAwait(false);
        }
    }

    private async Task AssertTerminalWithoutRetryAsync(SessionPeer spare)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken
        );
        var next = spare.Requests.ReadAsync(stop.Token).AsTask();
        try
        {
            var winner = await Task.WhenAny(Client.Manager.Completion, next)
                .WaitAsync(SessionTestSupport.Guard, stop.Token)
                .ConfigureAwait(false);
            Assert.Same(Client.Manager.Completion, winner);
            await Client.Manager.Completion.ConfigureAwait(false);
            Assert.Equal(0, spare.WrittenCount);
        }
        finally
        {
            await stop.CancelAsync().ConfigureAwait(false);
        }
    }

    private async Task AssertAdmissionClosedAsync(SessionPeer peer)
    {
        Assert.Equal(ZooKeeperSessionState.Restoring, Client.Session.State);
        var error = await Assert
            .ThrowsAsync<ZooKeeperException>(() =>
                Client.SyncAsync("/blocked", TestContext.Current.CancellationToken)
            )
            .ConfigureAwait(false);
        Assert.Equal(RequestOutcome.NotSent, error.Outcome);
        Assert.Null(error.Request.RequestId);
        Assert.Equal(0, peer.Requests.Count);
    }

    private static string[] RestorePaths(ReceivedPacket packet)
    {
        Assert.Equal(WatchProtocol.ResetXid, packet.Xid);
        var input = new JuteReader(packet.Bytes, ProtocolFixtures.Limits);
        input.ReadRequestHeader();
        Assert.Equal(Watermark, input.ReadInt64());
        Assert.Empty(input.ReadStringVector()!);
        Assert.Empty(input.ReadStringVector()!);
        Assert.Empty(input.ReadStringVector()!);
        var paths = input.ReadStringVector()!;
        Assert.Empty(input.ReadStringVector()!);
        input.EnsureEnd();
        return paths.Select(static path => path!).ToArray();
    }

    private static void AssertResume(ReceivedPacket packet)
    {
        Assert.True(packet.Handshake);
        var input = new JuteReader(packet.Bytes, ProtocolFixtures.Limits);
        input.ReadInt32();
        Assert.Equal(Watermark, input.ReadInt64());
        input.ReadInt32();
        Assert.Equal(123, input.ReadInt64());
    }

    private static void SessionEvent(SessionPeer peer, int state)
    {
        var bytes = new byte[PacketBytes];
        var output = new JuteWriter(bytes, ProtocolFixtures.Limits);
        output.WriteReplyHeader(
            new ReplyHeader(WatchProtocol.NotificationXid, -1, WatchProtocol.Success)
        );
        output.WriteInt32(WatchProtocol.SessionEvent);
        output.WriteInt32(state);
        output.WriteString(null);
        peer.Inject(bytes[..output.WrittenCount]);
    }
}
