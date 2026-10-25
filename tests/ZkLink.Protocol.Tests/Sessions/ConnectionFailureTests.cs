using ZkLink.Protocol;

namespace ZkLink.Protocol.Tests.Sessions;

public sealed class ConnectionFailureTests : IAsyncLifetime
{
    private readonly SessionFixture _fixture = new();
    private readonly List<SessionPeer> _peers = [];

    private SessionPeer Peer(int timeout = 600, long id = 123)
    {
        var peer = new SessionPeer { NegotiatedTimeout = timeout, Id = id };
        _peers.Add(peer);
        return peer;
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
    public async Task RejectedInitialHandshakeDoesNotCreateOrReplaceASession()
    {
        var peer = Peer(timeout: 0, id: 0);
        var error = await Assert
            .ThrowsAsync<ZooKeeperConnectionException>(() =>
                _fixture.ConnectAsync(
                    peer,
                    cancellationToken: TestContext.Current.CancellationToken
                )
            )
            .ConfigureAwait(true);
        Assert.Equal(ZooKeeperConnectionFailure.SessionRejected, error.Failure);
        Assert.True(peer.IsDisposed);
        Assert.Equal(1, _fixture.Connector.Attempts.Reader.Count);
    }

    [Fact(Timeout = 10000)]
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Reliability",
        "CA2025",
        Justification = "The connection Task is awaited by ThrowsAnyAsync before the token source is disposed; no client is returned on this cancellation path."
    )]
    public async Task CancellationOfInitialHandshakeCleansUpBeforeCompleting()
    {
        var peer = Peer();
        peer.ReplyHandshake = false;
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken
        );
        var pending = _fixture.ConnectAsync(peer, cancellationToken: stop.Token);
        await SessionTestSupport.NextAsync(peer).ConfigureAwait(true);
        await stop.CancelAsync().ConfigureAwait(true);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending).ConfigureAwait(true);
        Assert.True(peer.IsDisposed);
        Assert.True(pending.IsCanceled);
    }

    [Fact(Timeout = 10000)]
    public async Task InitialDeadlineBoundsAStalledHandshakeAcrossEndpointAttempts()
    {
        var peer = Peer();
        peer.ReplyHandshake = false;
        var pending = _fixture.ConnectAsync(
            peer,
            new ZooKeeperClientOptions
            {
                ConnectionString = "one:2181,two:2181",
                Tls = ZooKeeperTlsOptions.Disabled(),
                InitialConnectTimeout = TimeSpan.FromMilliseconds(100),
                ConnectAttemptTimeout = TimeSpan.FromMilliseconds(100),
            },
            TestContext.Current.CancellationToken
        );
        await SessionTestSupport.NextAsync(peer).ConfigureAwait(true);
        _fixture.Time.Advance(TimeSpan.FromMilliseconds(100));
        var error = await Assert
            .ThrowsAsync<ZooKeeperConnectionException>(() => SessionTestSupport.Bounded(pending))
            .ConfigureAwait(true);
        Assert.Equal(ZooKeeperConnectionFailure.Timeout, error.Failure);
        Assert.True(peer.IsDisposed);
    }

    [Fact(Timeout = 10000)]
    [Trait("Acceptance", "T08")]
    public async Task EndpointUpdatesAffectFutureAttemptsAndOldTransportIsClosedFirst()
    {
        var first = Peer();
        var second = Peer();
        var client = await _fixture
            .ConnectAsync(
                first,
                SessionFixture.Options(connection: "one:2181,two:2181"),
                TestContext.Current.CancellationToken
            )
            .ConfigureAwait(true);
        await SessionTestSupport.NextAsync(first).ConfigureAwait(true);
        await _fixture
            .Connector.Attempts.Reader.ReadAsync(TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        var replacement = new[] { new ZooKeeperEndpoint("replacement", 3210) };
        await client
            .UpdateEndpointsAsync(replacement, TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        replacement[0] = new ZooKeeperEndpoint("mutated", 1);
        _fixture.Connector.Add(second);
        var observer = new StateObserver(client);
        await using var observerLifetime = observer.ConfigureAwait(true);
        first.Drop();
        var attempt = await _fixture
            .Connector.Attempts.Reader.ReadAsync(TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        Assert.Equal("replacement", attempt.Host);
        Assert.Equal(3210, attempt.Port);
        await observer.WaitAsync(ZooKeeperSessionState.Connected).ConfigureAwait(true);
        // The observer's initial Connected can precede the reconnect; handshake is the transport barrier.
        await SessionTestSupport.NextAsync(second).ConfigureAwait(true);
        await SessionTestSupport
            .Bounded(client.WaitForConnectionAsync(TestContext.Current.CancellationToken))
            .ConfigureAwait(true);
        Assert.True(first.IsDisposed);
        Assert.Equal(123, client.Session.SessionId);
    }

    [Fact(Timeout = 10000)]
    [Trait("Acceptance", "T14")]
    public async Task WrongReplyIdFaultsTheSessionAndResolvesAllPendingCalls()
    {
        var peer = Peer();
        peer.ReplyApplications = false;
        var client = await _fixture
            .ConnectAsync(peer, cancellationToken: TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        await SessionTestSupport.NextAsync(peer).ConfigureAwait(true);
        var first = client.SyncAsync("/a", TestContext.Current.CancellationToken);
        var second = client.SyncAsync("/b", TestContext.Current.CancellationToken);
        var packet = await SessionTestSupport.NextAsync(peer).ConfigureAwait(true);
        await SessionTestSupport.NextAsync(peer).ConfigureAwait(true);
        peer.Inject(SessionPeer.Packet(packet.Xid + 10, path: "/a"));
        await SessionTestSupport.Bounded(client.Manager.Completion).ConfigureAwait(true);
        await Assert.ThrowsAsync<ZooKeeperException>(() => first).ConfigureAwait(true);
        await Assert.ThrowsAsync<ZooKeeperException>(() => second).ConfigureAwait(true);
        Assert.Equal(ZooKeeperSessionState.Faulted, client.Session.State);
        Assert.Equal(0, client.Manager.Scheduler.OwnedBytes);
        Assert.True(peer.IsDisposed);
    }

    [Fact(Timeout = 10000)]
    [Trait("Acceptance", "T12")]
    public async Task PartialWriteFailureIsUnknownAndNeverReplayed()
    {
        var peer = Peer();
        var client = await _fixture
            .ConnectAsync(
                peer,
                SessionFixture.Options(connection: "one:2181,two:2181"),
                TestContext.Current.CancellationToken
            )
            .ConfigureAwait(true);
        await SessionTestSupport.NextAsync(peer).ConfigureAwait(true);
        peer.FailWriteAt = peer.WrittenCount + 7;
        var request = client.SyncAsync("/partial", TestContext.Current.CancellationToken);
        var error = await Assert
            .ThrowsAsync<ZooKeeperException>(() => SessionTestSupport.Bounded(request))
            .ConfigureAwait(true);
        Assert.Equal(RequestOutcome.Unknown, error.Outcome);
        Assert.NotNull(error.Request.RequestId);
        Assert.Equal(0, peer.Requests.Count);
    }

    [Fact(Timeout = 10000)]
    [Trait("Acceptance", "T14")]
    public async Task IgnoredApplicationReplyForcesRecoveryEvenWhilePingsSucceed()
    {
        var first = Peer();
        var second = Peer();
        var client = await _fixture
            .ConnectAsync(
                first,
                SessionFixture.Options(connection: "one:2181,two:2181"),
                TestContext.Current.CancellationToken
            )
            .ConfigureAwait(true);
        await SessionTestSupport.NextAsync(first).ConfigureAwait(true);
        first.ReplyApplications = false;
        var request = client.SyncAsync("/ignored", TestContext.Current.CancellationToken);
        await SessionTestSupport.NextAsync(first).ConfigureAwait(true);
        _fixture.Time.Advance(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAsync<ZooKeeperTimeoutException>(() => request).ConfigureAwait(true);
        _fixture.Connector.Add(second);
        for (var i = 0; i < 3; i++)
        {
            await _fixture
                .Time.WaitForTimerAsync(
                    TimeSpan.FromMilliseconds((i + 1) * 200),
                    TestContext.Current.CancellationToken
                )
                .ConfigureAwait(true);
            _fixture.Time.Advance(TimeSpan.FromMilliseconds(i == 0 ? 100 : 200));
            var ping = await SessionTestSupport.NextAsync(first).ConfigureAwait(true);
            Assert.Equal(11, ping.Opcode);
            // The next pending read proves that the ping acknowledgment was processed.
            while (first.DrainReadWaits()) { }
            // Inject a second valid control observation to obtain a fresh reader barrier.
            first.Inject(SessionNotification());
            await SessionTestSupport.ReaderWaitingAsync(first).ConfigureAwait(true);
        }
        await _fixture
            .Time.WaitForTimerAsync(
                TimeSpan.FromMilliseconds(700),
                TestContext.Current.CancellationToken
            )
            .ConfigureAwait(true);
        _fixture.Time.Advance(TimeSpan.FromMilliseconds(100));
        await SessionTestSupport.NextAsync(second).ConfigureAwait(true);
        await SessionTestSupport
            .Bounded(client.WaitForConnectionAsync(TestContext.Current.CancellationToken))
            .ConfigureAwait(true);
        Assert.True(first.IsDisposed);
        Assert.Equal(2, client.Session.ConnectionGeneration);
        Assert.Equal(0, second.Requests.Count);
        Assert.Equal(0, client.Manager.Scheduler.OwnedBytes);
    }

    [Fact(Timeout = 10000)]
    [Trait("Acceptance", "T14")]
    public async Task DetachedTransportCannotRefreshLivenessOrEscalateAnOldReaderFailure()
    {
        var peer = Peer();
        var client = await _fixture
            .ConnectAsync(peer, cancellationToken: TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        client.Manager.TransportStopped(1, new(ZkLink.Sessions.RuntimeStop.Disconnected));
        _fixture.Time.Advance(TimeSpan.FromMilliseconds(50));
        client.Manager.Inbound(1, new ReplyHeader(1, 99, 0));
        client.Manager.TransportStopped(1, new(ZkLink.Sessions.RuntimeStop.Protocol));
        Assert.Equal(ZooKeeperSessionState.Disconnected, client.Session.State);
        Assert.Equal(0, client.Session.LastReplyZxid);
        Assert.Equal(TimeSpan.FromMilliseconds(50), client.Manager.InboundSilence);
    }

    private static byte[] SessionNotification()
    {
        var data = new byte[64];
        var writer = new JuteWriter(data, ProtocolFixtures.Limits);
        writer.WriteReplyHeader(new ReplyHeader(-1, -1, 0));
        writer.WriteInt32(-1);
        writer.WriteInt32(3);
        writer.WriteString(null);
        return data[..writer.WrittenCount];
    }
}
