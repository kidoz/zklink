using ZkLink.Protocol;

namespace ZkLink.Protocol.Tests.Sessions;

public sealed class SessionLifecycleTests : IAsyncLifetime
{
    private readonly SessionFixture _fixture = new();
    private readonly List<SessionPeer> _peers = [];

    private SessionPeer Peer()
    {
        var value = new SessionPeer();
        _peers.Add(value);
        return value;
    }

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        await _fixture.DisposeAsync().ConfigureAwait(true);
        foreach (var peer in _peers)
        {
            peer.Dispose();
        }
    }

    [Fact(Timeout = 10000)]
    [Trait("Acceptance", "T07")]
    public async Task ConnectUsesNewCredentialsAndNegotiatedTimeoutThenCloseIsAcknowledged()
    {
        var peer = Peer();
        var client = await SessionTestSupport
            .Bounded(
                _fixture.ConnectAsync(
                    peer,
                    cancellationToken: TestContext.Current.CancellationToken
                )
            )
            .ConfigureAwait(true);
        var handshake = await SessionTestSupport.NextAsync(peer).ConfigureAwait(true);
        Assert.True(handshake.Handshake);
        var reader = new JuteReader(handshake.Bytes, ProtocolFixtures.Limits);
        Assert.Equal(0, reader.ReadInt32());
        Assert.Equal(0, reader.ReadInt64());
        Assert.Equal(30000, reader.ReadInt32());
        Assert.Equal(0, reader.ReadInt64());
        Assert.Equal(new byte[16], reader.ReadBuffer());
        Assert.False(reader.ReadBoolean());
        reader.EnsureEnd();
        Assert.Equal(ZooKeeperSessionState.Connected, client.Session.State);
        Assert.Equal(123, client.Session.SessionId);
        Assert.Equal(TimeSpan.FromMilliseconds(600), client.Session.NegotiatedTimeout);
        Assert.Equal(1, client.Session.ConnectionGeneration);
        var close = client.CloseAsync(TestContext.Current.CancellationToken);
        var repeated = client.CloseAsync(TestContext.Current.CancellationToken);
        Assert.Same(close, repeated);
        await SessionTestSupport.Bounded(close).ConfigureAwait(true);
        var closePacket = await SessionTestSupport.NextAsync(peer).ConfigureAwait(true);
        Assert.Equal(-11, closePacket.Opcode);
        Assert.Equal(ZooKeeperCloseStatus.Acknowledged, client.Session.CloseStatus);
        Assert.Equal(ZooKeeperSessionState.Closed, client.Session.State);
        Assert.True(peer.IsDisposed);
        Assert.Equal(0, client.Manager.Scheduler.OwnedBytes);
    }

    [Fact(Timeout = 10000)]
    [Trait("Acceptance", "T08")]
    [Trait("Acceptance", "T10")]
    public async Task ResumePreservesSessionCredentialsAndWatermarkWithoutReplayingApplicationRequests()
    {
        var first = Peer();
        var second = Peer();
        _fixture.Connector.Add(first);
        _fixture.Connector.Add(second);
        var client = await SessionTestSupport
            .Bounded(
                ZooKeeperClient.ConnectCoreAsync(
                    ZkLink.Configuration.ClientOptionsSnapshot.Capture(
                        SessionFixture.Options(connection: "first:2181,second:2181")
                    ),
                    _fixture.Time,
                    _fixture.Connector,
                    new ZkLink.Sessions.SessionRandom(new Random(1)),
                    TestContext.Current.CancellationToken
                )
            )
            .ConfigureAwait(true);
        // This client is externally owned so cleanup remains explicit.
        await using var clientLifetime = client.ConfigureAwait(true);
        await SessionTestSupport.NextAsync(first).ConfigureAwait(true);
        await SessionTestSupport
            .Bounded(client.SyncAsync("/watermark", TestContext.Current.CancellationToken))
            .ConfigureAwait(true);
        await SessionTestSupport.NextAsync(first).ConfigureAwait(true);
        first.ReplyApplications = false;
        var uncertain = client.SyncAsync("/uncertain", TestContext.Current.CancellationToken);
        await SessionTestSupport.NextAsync(first).ConfigureAwait(true);
        var observer = new StateObserver(client);
        await using var observerLifetime = observer.ConfigureAwait(true);
        first.Drop();
        await observer.WaitAsync(ZooKeeperSessionState.Disconnected).ConfigureAwait(true);
        await observer.WaitAsync(ZooKeeperSessionState.Connected).ConfigureAwait(true);
        var failure = await Assert
            .ThrowsAsync<ZooKeeperException>(() => uncertain)
            .ConfigureAwait(true);
        Assert.Equal(RequestOutcome.Unknown, failure.Outcome);
        Assert.True(first.IsDisposed);
        var resumed = await SessionTestSupport.NextAsync(second).ConfigureAwait(true);
        Assert.True(resumed.Handshake);
        var reader = new JuteReader(resumed.Bytes, ProtocolFixtures.Limits);
        reader.ReadInt32();
        Assert.Equal(12, reader.ReadInt64());
        reader.ReadInt32();
        Assert.Equal(123, reader.ReadInt64());
        Assert.All(Assert.IsType<byte[]>(reader.ReadBuffer()), value => Assert.Equal(0x5a, value));
        Assert.Equal(2, client.Session.ConnectionGeneration);
        Assert.Equal(123, client.Session.SessionId);
        Assert.Equal(0, second.Requests.Count);
        await SessionTestSupport
            .Bounded(client.SyncAsync("/new", TestContext.Current.CancellationToken))
            .ConfigureAwait(true);
    }

    [Fact(Timeout = 10000)]
    [Trait("Acceptance", "T09")]
    public async Task HeartbeatsUseNegotiatedTimeAndSilenceEndsInLocalLost()
    {
        var peer = Peer();
        peer.ReplyPings = false;
        var client = await SessionTestSupport
            .Bounded(
                _fixture.ConnectAsync(
                    peer,
                    SessionFixture.Options(connection: "first:2181,second:2181"),
                    TestContext.Current.CancellationToken
                )
            )
            .ConfigureAwait(true);
        await SessionTestSupport.NextAsync(peer).ConfigureAwait(true);
        await SessionTestSupport.ReaderWaitingAsync(peer).ConfigureAwait(true);
        await _fixture
            .Connector.Attempts.Reader.ReadAsync(TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        var observer = new StateObserver(client);
        await using var observerLifetime = observer.ConfigureAwait(true);
        await _fixture
            .Time.WaitForTimerAsync(
                TimeSpan.FromMilliseconds(200),
                TestContext.Current.CancellationToken
            )
            .ConfigureAwait(true);
        _fixture.Time.Advance(TimeSpan.FromMilliseconds(200));
        var ping = await SessionTestSupport.NextAsync(peer).ConfigureAwait(true);
        Assert.Equal(-2, ping.Xid);
        Assert.Equal(11, ping.Opcode);
        _fixture.Time.Advance(TimeSpan.FromMilliseconds(200));
        await observer.WaitAsync(ZooKeeperSessionState.Disconnected).ConfigureAwait(true);
        // Observe the reconnect attempt before advancing its remaining session deadline.
        await _fixture
            .Connector.Attempts.Reader.ReadAsync(TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        _fixture.Time.Advance(TimeSpan.FromMilliseconds(200));
        await observer.WaitAsync(ZooKeeperSessionState.Lost).ConfigureAwait(true);
        Assert.Equal(ZooKeeperSessionState.Lost, client.Session.State);
        Assert.True(peer.IsDisposed);
        await Assert
            .ThrowsAsync<ZooKeeperSessionException>(() =>
                client.WaitForConnectionAsync(TestContext.Current.CancellationToken)
            )
            .ConfigureAwait(true);
    }

    [Fact(Timeout = 10000)]
    [Trait("Acceptance", "T10")]
    public async Task ServerExpiryIsTerminalAndNeedsNoClosePacket()
    {
        var peer = Peer();
        var client = await SessionTestSupport
            .Bounded(
                _fixture.ConnectAsync(
                    peer,
                    cancellationToken: TestContext.Current.CancellationToken
                )
            )
            .ConfigureAwait(true);
        await SessionTestSupport.NextAsync(peer).ConfigureAwait(true);
        var observer = new StateObserver(client);
        await using var observerLifetime = observer.ConfigureAwait(true);
        var bytes = new byte[64];
        var writer = new JuteWriter(bytes, ProtocolFixtures.Limits);
        writer.WriteReplyHeader(new ReplyHeader(-1, -1, 0));
        writer.WriteInt32(-1);
        writer.WriteInt32(-112);
        writer.WriteString(null);
        peer.Inject(bytes[..writer.WrittenCount]);
        await observer.WaitAsync(ZooKeeperSessionState.Expired).ConfigureAwait(true);
        await SessionTestSupport
            .Bounded(client.CloseAsync(TestContext.Current.CancellationToken))
            .ConfigureAwait(true);
        Assert.Equal(ZooKeeperCloseStatus.SessionExpired, client.Session.CloseStatus);
        Assert.Equal(ZooKeeperSessionState.Expired, client.Session.State);
        Assert.Equal(0, peer.Requests.Count);
    }

    [Fact(Timeout = 10000)]
    [Trait("Acceptance", "T14")]
    public async Task UnacknowledgedCloseTimesOutAfterCleanupAndPreservesItsFirstResult()
    {
        var peer = Peer();
        peer.ReplyClose = false;
        var client = await SessionTestSupport
            .Bounded(
                _fixture.ConnectAsync(
                    peer,
                    cancellationToken: TestContext.Current.CancellationToken
                )
            )
            .ConfigureAwait(true);
        await SessionTestSupport.NextAsync(peer).ConfigureAwait(true);
        var close = client.CloseAsync(TestContext.Current.CancellationToken);
        await SessionTestSupport.NextAsync(peer).ConfigureAwait(true);
        await _fixture
            .Time.WaitForTimerAsync(
                TimeSpan.FromMilliseconds(100),
                TestContext.Current.CancellationToken
            )
            .ConfigureAwait(true);
        _fixture.Time.Advance(TimeSpan.FromMilliseconds(100));
        var error = await Assert
            .ThrowsAsync<ZooKeeperCloseException>(() => SessionTestSupport.Bounded(close))
            .ConfigureAwait(true);
        Assert.Equal(RequestOutcome.Unknown, error.Outcome);
        Assert.False(error.CloseAcknowledged);
        Assert.True(peer.IsDisposed);
        Assert.Same(close, client.CloseAsync(TestContext.Current.CancellationToken));
        Assert.Equal(ZooKeeperCloseStatus.Unacknowledged, client.Session.CloseStatus);
        await client.DisposeAsync().ConfigureAwait(true);
    }

    [Fact(Timeout = 10000)]
    public async Task CancellationAfterInitialConnectDoesNotCancelTheEstablishedClient()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken
        );
        var peer = Peer();
        var client = await SessionTestSupport
            .Bounded(_fixture.ConnectAsync(peer, cancellationToken: cancellation.Token))
            .ConfigureAwait(true);
        await cancellation.CancelAsync().ConfigureAwait(true);
        await SessionTestSupport
            .Bounded(client.SyncAsync("/still-alive", TestContext.Current.CancellationToken))
            .ConfigureAwait(true);
        Assert.Equal(ZooKeeperSessionState.Connected, client.Session.State);
    }

    [Theory(Timeout = 10000)]
    [InlineData(false, RequestOutcome.NotSent)]
    [InlineData(true, RequestOutcome.Unknown)]
    [Trait("Acceptance", "T14")]
    public async Task CloseCancellationPreservesOutcomeAndReleasesResourcesBeforeCompleting(
        bool afterSend,
        RequestOutcome outcome
    )
    {
        var peer = Peer();
        peer.ReplyClose = false;
        var client = await _fixture
            .ConnectAsync(peer, cancellationToken: TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        await SessionTestSupport.NextAsync(peer).ConfigureAwait(true);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken
        );
        if (!afterSend)
        {
            await stop.CancelAsync().ConfigureAwait(true);
        }
        var close = client.CloseAsync(stop.Token);
        if (afterSend)
        {
            Assert.Equal(
                -11,
                (await SessionTestSupport.NextAsync(peer).ConfigureAwait(true)).Opcode
            );
            await stop.CancelAsync().ConfigureAwait(true);
        }
        var failure = await Assert
            .ThrowsAsync<ZooKeeperOperationCanceledException>(() => close)
            .ConfigureAwait(true);
        Assert.True(close.IsCanceled);
        Assert.Equal(outcome, failure.Outcome);
        Assert.True(peer.IsDisposed);
        Assert.Same(close, client.CloseAsync(TestContext.Current.CancellationToken));
        Assert.Equal(ZooKeeperCloseStatus.Unacknowledged, client.Session.CloseStatus);
    }

    [Fact(Timeout = 10000)]
    [Trait("Acceptance", "T31")]
    public async Task StateOverflowIsExplicitTerminalAndIndependentOfAStalledConsumer()
    {
        var peer = Peer();
        var client = await SessionTestSupport
            .Bounded(
                _fixture.ConnectAsync(
                    peer,
                    SessionFixture.Options(events: 1),
                    TestContext.Current.CancellationToken
                )
            )
            .ConfigureAwait(true);
        var subscription = client.SubscribeStateChanges();
        peer.Drop();
        await SessionTestSupport.Bounded(client.Manager.Completion).ConfigureAwait(true);
        // A reader waking after terminal cleanup observes the stored initial state then the explicit gap/fault.
        await using var lifetime = subscription.ConfigureAwait(true);
        await Assert
            .ThrowsAsync<ZooKeeperEventOverflowException>(async () =>
            {
                await foreach (
                    var _ in subscription
                        .ReadStatesAsync(TestContext.Current.CancellationToken)
                        .ConfigureAwait(true)
                ) { }
            })
            .ConfigureAwait(true);
        Assert.Equal(ZooKeeperSessionState.Faulted, client.Session.State);
        await Assert
            .ThrowsAsync<ZooKeeperEventOverflowException>(() =>
                client.WaitForConnectionAsync(TestContext.Current.CancellationToken)
            )
            .ConfigureAwait(true);
    }
}
