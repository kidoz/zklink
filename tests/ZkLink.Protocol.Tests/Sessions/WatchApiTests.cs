using System.Globalization;
using ZkLink.Protocol;
using ZkLink.Watches;

namespace ZkLink.Protocol.Tests.Sessions;

public sealed class WatchApiTests : IAsyncLifetime
{
    private const int TestTimeoutMilliseconds = 10_000;
    private const int StressTimeoutMilliseconds = 60_000;
    private const int ReplyBufferBytes = 4096;
    private const long Watermark = 12;
    private const long NotificationZxid = 999;
    private readonly SessionFixture _fixture = new();
    private readonly SessionPeer _peer = new() { ReplyApplications = false };
    private ZooKeeperClient Client => _fixture.Client!;

    public async ValueTask InitializeAsync()
    {
        await _fixture
            .ConnectAsync(
                _peer,
                SessionFixture.Options(connection: "first:2181,second:2181/app"),
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
    [InlineData(ZooKeeperWatchMode.OneShotExists, ZooKeeperWatchEventKind.NodeCreated, true)]
    [InlineData(ZooKeeperWatchMode.OneShotExists, ZooKeeperWatchEventKind.NodeDataChanged, false)]
    [InlineData(ZooKeeperWatchMode.OneShotExists, ZooKeeperWatchEventKind.NodeDeleted, false)]
    [InlineData(ZooKeeperWatchMode.OneShotData, ZooKeeperWatchEventKind.NodeDataChanged, false)]
    [InlineData(ZooKeeperWatchMode.OneShotData, ZooKeeperWatchEventKind.NodeDeleted, false)]
    [InlineData(
        ZooKeeperWatchMode.OneShotChildren,
        ZooKeeperWatchEventKind.NodeChildrenChanged,
        false
    )]
    [InlineData(ZooKeeperWatchMode.OneShotChildren, ZooKeeperWatchEventKind.NodeDeleted, false)]
    [InlineData(ZooKeeperWatchMode.OneShotData, ZooKeeperWatchEventKind.DataWatchRemoved, false)]
    [InlineData(
        ZooKeeperWatchMode.OneShotChildren,
        ZooKeeperWatchEventKind.ChildWatchRemoved,
        false
    )]
    [Trait("Acceptance", "T23")]
    [Trait("Acceptance", "T24")]
    public async Task ReaderRegistersBeforeTheFollowingEventAndBuffersExactlyOnce(
        ZooKeeperWatchMode mode,
        ZooKeeperWatchEventKind kind,
        bool missing
    )
    {
        var pending = Start(mode);
        var packet = await SessionTestSupport.NextAsync(_peer).ConfigureAwait(true);
        AssertWatchRequest(packet, mode, "/app/node");
        Reply(_peer, packet, mode, missing);
        Notify(_peer, kind, "/app/node");
        // A later ordinary response is a deterministic reader barrier, independent of caller continuation.
        var barrier = Client.SyncAsync("/barrier", TestContext.Current.CancellationToken);
        _peer.Reply(await SessionTestSupport.NextAsync(_peer).ConfigureAwait(true));
        await barrier.ConfigureAwait(true);
        var watch = await pending.ConfigureAwait(true);
        Assert.Equal(mode, watch.Mode);
        Assert.Equal("/node", watch.Path);
        var events = watch
            .ReadEventsAsync(TestContext.Current.CancellationToken)
            .GetAsyncEnumerator(TestContext.Current.CancellationToken);
        await using var lifetime = events.ConfigureAwait(true);
        Assert.True(await events.MoveNextAsync().ConfigureAwait(true));
        Assert.Equal(kind, events.Current.Kind);
        Assert.Equal("/node", events.Current.Path);
        Assert.Equal(watch.RegistrationId, events.Current.RegistrationId);
        Assert.Equal(NotificationZxid, events.Current.Zxid);
        Assert.DoesNotContain("/node", events.Current.ToString(), StringComparison.Ordinal);
        Assert.False(await events.MoveNextAsync().ConfigureAwait(true));
        Assert.Equal(0, Client.Manager.WatchCount);
    }

    [Fact(Timeout = TestTimeoutMilliseconds)]
    public async Task MissingExistsReturnsNullAndSessionEventsDoNotConsumeItsWatch()
    {
        var pending = Client.ExistsAndWatchAsync("/node", TestContext.Current.CancellationToken);
        Reply(
            _peer,
            await SessionTestSupport.NextAsync(_peer).ConfigureAwait(true),
            ZooKeeperWatchMode.OneShotExists,
            true
        );
        var watched = await pending.ConfigureAwait(true);
        Assert.Null(watched.Value);
        NotifyRaw(_peer, WatchProtocol.SessionEvent, null, WatchProtocol.ServerConnected, -1);
        Notify(_peer, ZooKeeperWatchEventKind.NodeCreated, "/app/node", -1);
        var events = watched
            .Watch.ReadEventsAsync(TestContext.Current.CancellationToken)
            .GetAsyncEnumerator(TestContext.Current.CancellationToken);
        await using var lifetime = events.ConfigureAwait(true);
        Assert.True(await events.MoveNextAsync().ConfigureAwait(true));
        Assert.Null(events.Current.Zxid);
        Assert.False(await events.MoveNextAsync().ConfigureAwait(true));
    }

    [Theory(Timeout = TestTimeoutMilliseconds)]
    [InlineData(ZooKeeperWatchMode.OneShotData)]
    [InlineData(ZooKeeperWatchMode.OneShotChildren)]
    public async Task FailedReadCreatesNoSuccessfulSubscription(ZooKeeperWatchMode mode)
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        var pending = Start(mode);
        _peer.Reply(
            await SessionTestSupport.NextAsync(_peer).ConfigureAwait(true),
            (int)ZooKeeperErrorCode.NoNode
        );
        await Assert.ThrowsAsync<ZooKeeperException>(() => pending).ConfigureAwait(true);
        Assert.Equal(0, Client.Manager.WatchCount);
        Assert.Equal(0, _peer.Requests.Count);
    }

    [Theory(Timeout = TestTimeoutMilliseconds)]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Acceptance", "T28")]
    public async Task CanceledSentExistsRetainsIntentAndCleansSuccessfulLateRegistration(
        bool missing
    )
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken
        );
        var pending = Client.ExistsAndWatchAsync("/node", stop.Token);
        var packet = await SessionTestSupport.NextAsync(_peer).ConfigureAwait(true);
        await stop.CancelAsync().ConfigureAwait(true);
        var failure = await Assert
            .ThrowsAsync<ZooKeeperOperationCanceledException>(() => pending)
            .ConfigureAwait(true);
        Assert.Equal(RequestOutcome.Unknown, failure.Outcome);
        Assert.True(pending.IsCanceled);
        Reply(_peer, packet, ZooKeeperWatchMode.OneShotExists, missing);
        var cleanup = await SessionTestSupport.NextAsync(_peer).ConfigureAwait(true);
        AssertRemoval(cleanup, "/app/node", WatchProtocol.Data);
        _peer.Reply(cleanup);
        await BarrierAsync().ConfigureAwait(true);
        Assert.Equal(0, Client.Manager.WatchCount);
        Assert.Equal(0, Client.Manager.Scheduler.OwnedBytes);
    }

    [Fact(Timeout = TestTimeoutMilliseconds)]
    public async Task CanceledQueuedReadNeverInstallsAWatch()
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken
        );
        await stop.CancelAsync().ConfigureAwait(true);
        var pending = Client.ExistsAndWatchAsync("/node", stop.Token);
        var failure = await Assert
            .ThrowsAsync<ZooKeeperOperationCanceledException>(() => pending)
            .ConfigureAwait(true);
        Assert.Equal(RequestOutcome.NotSent, failure.Outcome);
        Assert.Equal(0, Client.Manager.WatchCount);
        Assert.Equal(0, _peer.Requests.Count);
    }

    [Fact(Timeout = TestTimeoutMilliseconds)]
    [Trait("Acceptance", "T29")]
    public async Task RemovingOneSharedHandleRetainsTheOtherAndBothDataCategoriesShareRemoval()
    {
        var first = await RegisterAsync(ZooKeeperWatchMode.OneShotExists, missing: true)
            .ConfigureAwait(true);
        var secondPending = Start(ZooKeeperWatchMode.OneShotData);
        var secondPacket = await SessionTestSupport.NextAsync(_peer).ConfigureAwait(true);
        var result = await first
            .RemoveAsync(WatchRemovalMode.ServerConfirmed, TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        Assert.Equal(WatchRemovalStatus.SharedRegistrationRetained, result.Status);
        Assert.Equal(0, _peer.Requests.Count);
        Reply(_peer, secondPacket, ZooKeeperWatchMode.OneShotData, false);
        var second = await secondPending.ConfigureAwait(true);
        Notify(_peer, ZooKeeperWatchEventKind.NodeDataChanged, "/app/node");
        var firstEvents = first
            .ReadEventsAsync(TestContext.Current.CancellationToken)
            .GetAsyncEnumerator(TestContext.Current.CancellationToken);
        await using var firstLifetime = firstEvents.ConfigureAwait(true);
        Assert.False(await firstEvents.MoveNextAsync().ConfigureAwait(true));
        var secondEvents = second
            .ReadEventsAsync(TestContext.Current.CancellationToken)
            .GetAsyncEnumerator(TestContext.Current.CancellationToken);
        await using var secondLifetime = secondEvents.ConfigureAwait(true);
        Assert.True(await secondEvents.MoveNextAsync().ConfigureAwait(true));
        Assert.Equal(second.RegistrationId, secondEvents.Current.RegistrationId);
    }

    [Theory(Timeout = TestTimeoutMilliseconds)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConfirmedRemovalReportsAcknowledgmentOrNoWatcherAndCannotDeliverLater(
        bool noWatcher
    )
    {
        var watch = await RegisterAsync(ZooKeeperWatchMode.OneShotChildren).ConfigureAwait(true);
        var pending = watch.RemoveAsync(
            WatchRemovalMode.ServerConfirmed,
            TestContext.Current.CancellationToken
        );
        var packet = await SessionTestSupport.NextAsync(_peer).ConfigureAwait(true);
        AssertRemoval(packet, "/app/node", WatchProtocol.Children);
        Notify(_peer, ZooKeeperWatchEventKind.NodeChildrenChanged, "/app/node");
        _peer.Reply(packet, noWatcher ? (int)ZooKeeperErrorCode.NoWatcher : WatchProtocol.Success);
        Assert.Equal(
            noWatcher ? WatchRemovalStatus.NoWatcher : WatchRemovalStatus.ServerConfirmed,
            (await pending.ConfigureAwait(true)).Status
        );
        var events = watch
            .ReadEventsAsync(TestContext.Current.CancellationToken)
            .GetAsyncEnumerator(TestContext.Current.CancellationToken);
        await using var lifetime = events.ConfigureAwait(true);
        Assert.False(await events.MoveNextAsync().ConfigureAwait(true));
    }

    [Fact(Timeout = TestTimeoutMilliseconds)]
    public async Task LocalDisposalUsesReservedCleanupCapacityAndDoesNotWaitForAcknowledgment()
    {
        var watch = await RegisterAsync(ZooKeeperWatchMode.OneShotData).ConfigureAwait(true);
        const int applicationCapacity = 4;
        var blocked = new List<(Task Task, ReceivedPacket Packet)>();
        for (var index = 0; index < applicationCapacity; index++)
        {
            var task = Client.SyncAsync("/held", TestContext.Current.CancellationToken);
            blocked.Add((task, await SessionTestSupport.NextAsync(_peer).ConfigureAwait(true)));
        }
        await watch.DisposeAsync().ConfigureAwait(true);
        var packet = await SessionTestSupport.NextAsync(_peer).ConfigureAwait(true);
        AssertRemoval(packet, "/app/node", WatchProtocol.Data);
        foreach (var item in blocked)
            _peer.Reply(item.Packet);
        _peer.Reply(packet);
        foreach (var item in blocked)
            await item.Task.ConfigureAwait(true);
        await BarrierAsync().ConfigureAwait(true);
        Assert.Equal(0, Client.Manager.WatchCount);
    }

    [Fact(Timeout = TestTimeoutMilliseconds)]
    [Trait("Acceptance", "T26")]
    public async Task RestoreUsesAcknowledgedCategoriesAndWatermarkBeforeReadinessAndDispatchesEarlyEvents()
    {
        var exists = await RegisterAsync(ZooKeeperWatchMode.OneShotExists, "/absent", true)
            .ConfigureAwait(true);
        var data = await RegisterAsync(ZooKeeperWatchMode.OneShotData, "/data")
            .ConfigureAwait(true);
        var child = await RegisterAsync(ZooKeeperWatchMode.OneShotChildren, "/children")
            .ConfigureAwait(true);
        using var second = new SessionPeer { ReplyApplications = false, ReplyRestores = false };
        _fixture.Connector.Add(second);
        var states = new StateObserver(Client);
        await using var statesLifetime = states.ConfigureAwait(true);
        await states.WaitAsync(ZooKeeperSessionState.Connected).ConfigureAwait(true);
        _peer.Drop();
        await SessionTestSupport.NextAsync(second).ConfigureAwait(true);
        var reset = await SessionTestSupport.NextAsync(second).ConfigureAwait(true);
        var input = new JuteReader(reset.Bytes, ProtocolFixtures.Limits);
        Assert.Equal(WatchProtocol.ResetXid, input.ReadRequestHeader().Xid);
        Assert.Equal(Watermark, input.ReadInt64());
        Assert.Equal("/app/data", Assert.Single(input.ReadStringVector()!));
        Assert.Equal("/app/absent", Assert.Single(input.ReadStringVector()!));
        Assert.Equal("/app/children", Assert.Single(input.ReadStringVector()!));
        input.EnsureEnd();
        Assert.Equal(ZooKeeperSessionState.Restoring, Client.Session.State);
        var ready = Client.WaitForConnectionAsync(TestContext.Current.CancellationToken);
        Assert.False(ready.IsCompleted);
        Notify(second, ZooKeeperWatchEventKind.NodeDataChanged, "/app/data");
        var events = data.ReadEventsAsync(TestContext.Current.CancellationToken)
            .GetAsyncEnumerator(TestContext.Current.CancellationToken);
        await using var eventsLifetime = events.ConfigureAwait(true);
        Assert.True(await events.MoveNextAsync().ConfigureAwait(true));
        Assert.Equal(Watermark, Client.Session.LastReplyZxid);
        await child.DisposeAsync().ConfigureAwait(true); // A captured restore must never revive it.
        second.Reply(reset);
        await ready.ConfigureAwait(true);
        Assert.Equal(2, Client.Session.ConnectionGeneration);
        Assert.Equal(Watermark, Client.Session.LastReplyZxid);
        var cleanup = await SessionTestSupport.NextAsync(second).ConfigureAwait(true);
        Assert.Equal((int)ZooKeeperOperation.RemoveWatches, cleanup.Opcode);
        second.Reply(cleanup);
        // The consumed data category may also produce a harmless NoWatcher cleanup.
        second.ReplyApplications = true;
        while (second.Requests.TryRead(out var extra))
            second.Reply(extra);
        await exists.DisposeAsync().ConfigureAwait(true);
    }

    [Fact(Timeout = TestTimeoutMilliseconds)]
    [Trait("Acceptance", "T30")]
    public async Task FanoutOverflowFaultsTheClientAndCompletesStreamsExplicitly()
    {
        var fixture = new SessionFixture();
        await using var fixtureLifetime = fixture.ConfigureAwait(true);
        using var peer = new SessionPeer { ReplyApplications = false };
        var client = await fixture
            .ConnectAsync(
                peer,
                SessionFixture.Options(events: 1),
                TestContext.Current.CancellationToken
            )
            .ConfigureAwait(true);
        await SessionTestSupport.NextAsync(peer).ConfigureAwait(true);
        var one = client.ExistsAndWatchAsync("/node", TestContext.Current.CancellationToken);
        Reply(
            peer,
            await SessionTestSupport.NextAsync(peer).ConfigureAwait(true),
            ZooKeeperWatchMode.OneShotExists,
            true
        );
        var first = (await one.ConfigureAwait(true)).Watch;
        var two = client.ExistsAndWatchAsync("/node", TestContext.Current.CancellationToken);
        Reply(
            peer,
            await SessionTestSupport.NextAsync(peer).ConfigureAwait(true),
            ZooKeeperWatchMode.OneShotExists,
            true
        );
        var second = (await two.ConfigureAwait(true)).Watch;
        Notify(peer, ZooKeeperWatchEventKind.NodeCreated, "/node");
        await SessionTestSupport.Bounded(client.Manager.Completion).ConfigureAwait(true);
        Assert.Equal(ZooKeeperSessionState.Faulted, client.Session.State);
        await Assert
            .ThrowsAsync<ZooKeeperEventOverflowException>(() =>
                client.WaitForConnectionAsync(TestContext.Current.CancellationToken)
            )
            .ConfigureAwait(true);
        foreach (var watch in new[] { first, second })
        {
            var events = watch
                .ReadEventsAsync(TestContext.Current.CancellationToken)
                .GetAsyncEnumerator(TestContext.Current.CancellationToken);
            await using var lifetime = events.ConfigureAwait(true);
            await Assert
                .ThrowsAsync<ZooKeeperEventOverflowException>(() => events.MoveNextAsync().AsTask())
                .ConfigureAwait(true);
        }
    }

    [Fact(Timeout = StressTimeoutMilliseconds)]
    [Trait("Acceptance", "T27")]
    public async Task TenThousandLongUnicodeWatchesRestoreAcrossBoundedFramesWithOneWatermark()
    {
        const int watchCount = 10_000;
        for (var index = 0; index < watchCount; index++)
        {
            while (_peer.DrainReadWaits()) { }
            string path =
                "/w-" + index.ToString(CultureInfo.InvariantCulture) + "-" + new string('é', 80);
            await RegisterAsync((ZooKeeperWatchMode)(index % 3), path, index % 3 == 0)
                .ConfigureAwait(true);
        }
        using var second = new SessionPeer { ReplyApplications = false };
        _fixture.Connector.Add(second);
        _peer.Drop();
        await SessionTestSupport.NextAsync(second).ConfigureAwait(true);
        var restored = new HashSet<string>(StringComparer.Ordinal);
        var frames = 0;
        while (restored.Count < watchCount)
        {
            var packet = await SessionTestSupport.NextAsync(second).ConfigureAwait(true);
            Assert.True(packet.Bytes.Length <= WatchProtocol.RestoreTargetBytes);
            var input = new JuteReader(packet.Bytes, ProtocolFixtures.Limits);
            Assert.Equal(WatchProtocol.ResetXid, input.ReadRequestHeader().Xid);
            Assert.Equal(Watermark, input.ReadInt64());
            for (var category = 0; category < 3; category++)
                foreach (var path in input.ReadStringVector()!)
                {
                    Assert.StartsWith("/app/w-", path, StringComparison.Ordinal);
                    Assert.True(restored.Add(path!));
                }
            input.EnsureEnd();
            frames++;
        }
        await Client
            .WaitForConnectionAsync(TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        Assert.True(frames > 1);
        Assert.Equal(watchCount, Client.Manager.WatchCount);
        Assert.Equal(Watermark, Client.Session.LastReplyZxid);
    }

    [Fact(Timeout = TestTimeoutMilliseconds)]
    public async Task UnconfirmedLostResponseIsNotRestored()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        await RegisterAsync(ZooKeeperWatchMode.OneShotData, "/confirmed").ConfigureAwait(true);
        var uncertain = Client.ExistsAndWatchAsync(
            "/uncertain",
            TestContext.Current.CancellationToken
        );
        await SessionTestSupport.NextAsync(_peer).ConfigureAwait(true);
        using var second = new SessionPeer { ReplyApplications = true };
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
        Assert.Equal("/app/confirmed", Assert.Single(input.ReadStringVector()!));
        Assert.Empty(input.ReadStringVector()!);
        Assert.Empty(input.ReadStringVector()!);
        await Client
            .WaitForConnectionAsync(TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        Assert.Equal(1, Client.Manager.WatchCount);
    }

    [Fact(Timeout = TestTimeoutMilliseconds)]
    public async Task DisposalBeforeRestoreSnapshotExcludesThePathWithoutReplayingCleanup()
    {
        var watch = await RegisterAsync(ZooKeeperWatchMode.OneShotData).ConfigureAwait(true);
        using var second = new SessionPeer { ReplyApplications = true, ReplyHandshake = false };
        _fixture.Connector.Add(second);
        _peer.Drop();
        await SessionTestSupport.NextAsync(second).ConfigureAwait(true);
        await watch.DisposeAsync().ConfigureAwait(true);
        second.HandshakeReply();
        await Client
            .WaitForConnectionAsync(TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        Assert.Equal(0, Client.Manager.WatchCount);
        Assert.Equal(0, second.Requests.Count);
    }

    [Fact(Timeout = TestTimeoutMilliseconds)]
    public async Task WatchAndStateQueuesUseTheSameGlobalEventBudget()
    {
        var fixture = new SessionFixture();
        await using var fixtureLifetime = fixture.ConfigureAwait(true);
        using var peer = new SessionPeer { ReplyApplications = false };
        var client = await fixture
            .ConnectAsync(
                peer,
                SessionFixture.Options(events: 1),
                TestContext.Current.CancellationToken
            )
            .ConfigureAwait(true);
        await SessionTestSupport.NextAsync(peer).ConfigureAwait(true);
        var states = client.SubscribeStateChanges();
        await using var statesLifetime = states.ConfigureAwait(true);
        var pending = client.ExistsAndWatchAsync("/node", TestContext.Current.CancellationToken);
        Reply(
            peer,
            await SessionTestSupport.NextAsync(peer).ConfigureAwait(true),
            ZooKeeperWatchMode.OneShotExists,
            true
        );
        var watched = await pending.ConfigureAwait(true);
        Notify(peer, ZooKeeperWatchEventKind.NodeCreated, "/node");
        await SessionTestSupport.Bounded(client.Manager.Completion).ConfigureAwait(true);
        Assert.Equal(ZooKeeperSessionState.Faulted, client.Session.State);
        var events = watched
            .Watch.ReadEventsAsync(TestContext.Current.CancellationToken)
            .GetAsyncEnumerator(TestContext.Current.CancellationToken);
        await using var lifetime = events.ConfigureAwait(true);
        await Assert
            .ThrowsAsync<ZooKeeperEventOverflowException>(() => events.MoveNextAsync().AsTask())
            .ConfigureAwait(true);
    }

    [Fact(Timeout = TestTimeoutMilliseconds)]
    public async Task WatchCapacityBoundsPendingCleanupAndLongPathsMustFitRestoration()
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
        var first = client.ExistsAndWatchAsync("/node", TestContext.Current.CancellationToken);
        Reply(
            peer,
            await SessionTestSupport.NextAsync(peer).ConfigureAwait(true),
            ZooKeeperWatchMode.OneShotExists,
            true
        );
        var watch = (await first.ConfigureAwait(true)).Watch;
        var failure = Assert.Throws<ZooKeeperBackpressureException>(() =>
        {
            _ = client.ExistsAndWatchAsync("/second", TestContext.Current.CancellationToken);
        });
        Assert.Equal(RequestOutcome.NotSent, failure.Outcome);
        await watch.DisposeAsync().ConfigureAwait(true);
        peer.Reply(await SessionTestSupport.NextAsync(peer).ConfigureAwait(true));
        var barrier = client.SyncAsync("/barrier", TestContext.Current.CancellationToken);
        peer.Reply(await SessionTestSupport.NextAsync(peer).ConfigureAwait(true));
        await barrier.ConfigureAwait(true);
        var next = client.ExistsAndWatchAsync("/second", TestContext.Current.CancellationToken);
        Reply(
            peer,
            await SessionTestSupport.NextAsync(peer).ConfigureAwait(true),
            ZooKeeperWatchMode.OneShotExists,
            true
        );
        await next.ConfigureAwait(true);
        string tooLong =
            "/"
            + new string(
                'a',
                ProtocolLimits.DefaultMaxFrameBytes - WatchProtocol.RestoreFrameOverhead
            );
        Assert.Throws<InvalidDataException>(() =>
        {
            _ = Client.ExistsAndWatchAsync(tooLong, TestContext.Current.CancellationToken);
        });
    }

    [Theory(Timeout = TestTimeoutMilliseconds)]
    [InlineData(ZooKeeperErrorCode.NoNode)]
    [InlineData(ZooKeeperErrorCode.NoAuth)]
    public async Task CanceledReadWithLateFailureDoesNotScheduleRemoval(ZooKeeperErrorCode code)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken
        );
        var pending = Client.GetDataAndWatchAsync("/node", stop.Token);
        var packet = await SessionTestSupport.NextAsync(_peer).ConfigureAwait(true);
        await stop.CancelAsync().ConfigureAwait(true);
        await Assert
            .ThrowsAsync<ZooKeeperOperationCanceledException>(() => pending)
            .ConfigureAwait(true);
        _peer.Reply(packet, (int)code);
        await BarrierAsync().ConfigureAwait(true);
        Assert.Equal(0, Client.Manager.WatchCount);
        Assert.Equal(0, _peer.Requests.Count);
    }

    [Fact(Timeout = TestTimeoutMilliseconds)]
    public async Task CanceledServerConfirmationNeverRevivesLocalDelivery()
    {
        var watch = await RegisterAsync(ZooKeeperWatchMode.OneShotData).ConfigureAwait(true);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken
        );
        var remove = watch.RemoveAsync(WatchRemovalMode.ServerConfirmed, stop.Token);
        var packet = await SessionTestSupport.NextAsync(_peer).ConfigureAwait(true);
        await stop.CancelAsync().ConfigureAwait(true);
        var error = await Assert
            .ThrowsAsync<ZooKeeperOperationCanceledException>(() => remove)
            .ConfigureAwait(true);
        Assert.Equal(RequestOutcome.Unknown, error.Outcome);
        Notify(_peer, ZooKeeperWatchEventKind.NodeDataChanged, "/app/node");
        _peer.Reply(packet);
        await BarrierAsync().ConfigureAwait(true);
        var events = watch
            .ReadEventsAsync(TestContext.Current.CancellationToken)
            .GetAsyncEnumerator(TestContext.Current.CancellationToken);
        await using var lifetime = events.ConfigureAwait(true);
        Assert.False(await events.MoveNextAsync().ConfigureAwait(true));
    }

    [Fact(Timeout = TestTimeoutMilliseconds)]
    public async Task SecondConsumerFailsWithoutRemovingTheFirstConsumersRegistration()
    {
        var watch = await RegisterAsync(ZooKeeperWatchMode.OneShotData).ConfigureAwait(true);
        var first = watch
            .ReadEventsAsync(TestContext.Current.CancellationToken)
            .GetAsyncEnumerator(TestContext.Current.CancellationToken);
        await using var firstLifetime = first.ConfigureAwait(true);
        var pending = first.MoveNextAsync().AsTask();
        var second = watch
            .ReadEventsAsync(TestContext.Current.CancellationToken)
            .GetAsyncEnumerator(TestContext.Current.CancellationToken);
        await using var secondLifetime = second.ConfigureAwait(true);
        await Assert
            .ThrowsAsync<InvalidOperationException>(() => second.MoveNextAsync().AsTask())
            .ConfigureAwait(true);
        Notify(_peer, ZooKeeperWatchEventKind.NodeDataChanged, "/app/node");
        Assert.True(await pending.ConfigureAwait(true));
        await BarrierAsync().ConfigureAwait(true);
        Assert.False(await first.MoveNextAsync().ConfigureAwait(true));
    }

    [Fact(Timeout = TestTimeoutMilliseconds)]
    public async Task TimedOutWatchedReadCleansItsSuccessfulLateRegistration()
    {
        var pending = Client.ExistsAndWatchAsync("/node", TestContext.Current.CancellationToken);
        var packet = await SessionTestSupport.NextAsync(_peer).ConfigureAwait(true);
        _fixture.Time.Advance(SessionFixture.Options().OperationTimeout);
        var error = await Assert
            .ThrowsAsync<ZooKeeperTimeoutException>(() => pending)
            .ConfigureAwait(true);
        Assert.Equal(RequestOutcome.Unknown, error.Outcome);
        Reply(_peer, packet, ZooKeeperWatchMode.OneShotExists, true);
        var cleanup = await SessionTestSupport.NextAsync(_peer).ConfigureAwait(true);
        AssertRemoval(cleanup, "/app/node", WatchProtocol.Data);
        _peer.Reply(cleanup);
        await BarrierAsync().ConfigureAwait(true);
        Assert.Equal(0, Client.Manager.WatchCount);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(1_000_001)]
    public void WatchLimitMustBeFiniteAndPositive(int limit)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ZkLink.Configuration.ClientOptionsSnapshot.Capture(
                SessionFixture.Options(watches: limit)
            )
        );
    }

    [Fact(Timeout = TestTimeoutMilliseconds)]
    public async Task UnsentCleanupTimeoutRetainsItsBoundAndRetriesBeforeReleasingCapacity()
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
        var registration = client.ExistsAndWatchAsync(
            "/node",
            TestContext.Current.CancellationToken
        );
        Reply(
            peer,
            await SessionTestSupport.NextAsync(peer).ConfigureAwait(true),
            ZooKeeperWatchMode.OneShotExists,
            true
        );
        var watch = (await registration.ConfigureAwait(true)).Watch;
        peer.HoldApplicationWrite = true;
        var blocked = client.SyncAsync("/held", TestContext.Current.CancellationToken);
        var blockedPacket = await SessionTestSupport.NextAsync(peer).ConfigureAwait(true);
        await SessionTestSupport.Bounded(peer.ApplicationWriteHeld).ConfigureAwait(true);
        await watch.DisposeAsync().ConfigureAwait(true);
        peer.HoldPingWrite = true;
        client.Manager.Scheduler.QueuePing();
        peer.ReleaseWrite();
        var ping = await SessionTestSupport.NextAsync(peer).ConfigureAwait(true);
        Assert.Equal(new PingRequest().Opcode, ping.Opcode);
        await SessionTestSupport.Bounded(peer.PingWriteHeld).ConfigureAwait(true);
        fixture.Time.Advance(SessionFixture.Options().OperationTimeout);
        await Assert.ThrowsAsync<ZooKeeperTimeoutException>(() => blocked).ConfigureAwait(true);
        Assert.Throws<ZooKeeperBackpressureException>(() =>
        {
            _ = client.ExistsAndWatchAsync("/new", TestContext.Current.CancellationToken);
        });
        peer.ReleasePingWrite();
        var cleanup = await SessionTestSupport.NextAsync(peer).ConfigureAwait(true);
        AssertRemoval(cleanup, "/node", WatchProtocol.Data);
        peer.Reply(blockedPacket);
        peer.Reply(cleanup);
        var barrier = client.SyncAsync("/barrier", TestContext.Current.CancellationToken);
        peer.Reply(await SessionTestSupport.NextAsync(peer).ConfigureAwait(true));
        await barrier.ConfigureAwait(true);
        var next = client.ExistsAndWatchAsync("/new", TestContext.Current.CancellationToken);
        Reply(
            peer,
            await SessionTestSupport.NextAsync(peer).ConfigureAwait(true),
            ZooKeeperWatchMode.OneShotExists,
            true
        );
        await next.ConfigureAwait(true);
    }

    [Fact(Timeout = TestTimeoutMilliseconds)]
    public async Task FailedPendingSharerCannotDiscardAnOlderCleanupIntent()
    {
        var watch = await RegisterAsync(ZooKeeperWatchMode.OneShotData).ConfigureAwait(true);
        _peer.HoldApplicationWrite = true;
        var barrier = Client.SyncAsync("/held", TestContext.Current.CancellationToken);
        var held = await SessionTestSupport.NextAsync(_peer).ConfigureAwait(true);
        await SessionTestSupport.Bounded(_peer.ApplicationWriteHeld).ConfigureAwait(true);
        await watch.DisposeAsync().ConfigureAwait(true);
        var pending = Client.GetDataAndWatchAsync("/node", TestContext.Current.CancellationToken);
        _peer.ReleaseWrite();
        var read = await SessionTestSupport.NextAsync(_peer).ConfigureAwait(true);
        Assert.Equal((int)ZooKeeperOperation.GetData, read.Opcode);
        _peer.Reply(held);
        await barrier.ConfigureAwait(true);
        _peer.Reply(read, (int)ZooKeeperErrorCode.NoAuth);
        await Assert.ThrowsAsync<ZooKeeperException>(() => pending).ConfigureAwait(true);
        var cleanup = await SessionTestSupport.NextAsync(_peer).ConfigureAwait(true);
        AssertRemoval(cleanup, "/app/node", WatchProtocol.Data);
        _peer.Reply(cleanup);
        await BarrierAsync().ConfigureAwait(true);
        Assert.Equal(0, Client.Manager.WatchCount);
    }

    private Task<ZooKeeperWatch> Start(ZooKeeperWatchMode mode, string path = "/node") =>
        mode switch
        {
            ZooKeeperWatchMode.OneShotExists => OnlyWatchAsync(
                Client.ExistsAndWatchAsync(path, TestContext.Current.CancellationToken)
            ),
            ZooKeeperWatchMode.OneShotData => OnlyWatchAsync(
                Client.GetDataAndWatchAsync(path, TestContext.Current.CancellationToken)
            ),
            _ => OnlyWatchAsync(
                Client.GetChildrenAndWatchAsync(path, TestContext.Current.CancellationToken)
            ),
        };

    private static async Task<ZooKeeperWatch> OnlyWatchAsync<T>(Task<WatchedResult<T>> task) =>
        (await task.ConfigureAwait(false)).Watch;

    private async Task<ZooKeeperWatch> RegisterAsync(
        ZooKeeperWatchMode mode,
        string path = "/node",
        bool missing = false
    )
    {
        var pending = Start(mode, path);
        Reply(
            _peer,
            await SessionTestSupport.NextAsync(_peer).ConfigureAwait(false),
            mode,
            missing
        );
        return await pending.ConfigureAwait(false);
    }

    private async Task BarrierAsync()
    {
        var task = Client.SyncAsync("/barrier", TestContext.Current.CancellationToken);
        _peer.Reply(await SessionTestSupport.NextAsync(_peer).ConfigureAwait(false));
        await task.ConfigureAwait(false);
    }

    private static void AssertWatchRequest(
        ReceivedPacket packet,
        ZooKeeperWatchMode mode,
        string path
    )
    {
        var input = new JuteReader(packet.Bytes, ProtocolFixtures.Limits);
        var header = input.ReadRequestHeader();
        Assert.Equal(
            (int)(
                mode == ZooKeeperWatchMode.OneShotExists ? ZooKeeperOperation.Exists
                : mode == ZooKeeperWatchMode.OneShotData ? ZooKeeperOperation.GetData
                : ZooKeeperOperation.GetChildren
            ),
            header.Opcode
        );
        Assert.Equal(path, input.ReadString());
        Assert.True(input.ReadBoolean());
        input.EnsureEnd();
    }

    private static void AssertRemoval(ReceivedPacket packet, string path, int category)
    {
        Assert.Equal((int)ZooKeeperOperation.RemoveWatches, packet.Opcode);
        var input = new JuteReader(packet.Bytes, ProtocolFixtures.Limits);
        input.ReadRequestHeader();
        Assert.Equal(path, input.ReadString());
        Assert.Equal(category, input.ReadInt32());
        input.EnsureEnd();
    }

    private static void Reply(
        SessionPeer peer,
        ReceivedPacket packet,
        ZooKeeperWatchMode mode,
        bool missing
    )
    {
        var bytes = new byte[ReplyBufferBytes];
        var writer = new JuteWriter(bytes, ProtocolFixtures.Limits);
        writer.WriteReplyHeader(
            new ReplyHeader(
                packet.Xid,
                Watermark,
                missing ? (int)ZooKeeperErrorCode.NoNode : WatchProtocol.Success
            )
        );
        if (!missing)
        {
            if (mode == ZooKeeperWatchMode.OneShotData)
                writer.WriteNullBuffer();
            if (mode == ZooKeeperWatchMode.OneShotChildren)
                writer.WriteInt32(0);
            writer.WriteStat(ProtocolFixtures.Stat);
        }
        peer.Inject(bytes[..writer.WrittenCount]);
    }

    private static void Notify(
        SessionPeer peer,
        ZooKeeperWatchEventKind kind,
        string path,
        long zxid = NotificationZxid
    ) => NotifyRaw(peer, (int)kind, path, WatchProtocol.ServerConnected, zxid);

    private static void NotifyRaw(SessionPeer peer, int type, string? path, int state, long zxid)
    {
        var bytes = new byte[ReplyBufferBytes];
        var writer = new JuteWriter(bytes, ProtocolFixtures.Limits);
        writer.WriteReplyHeader(
            new ReplyHeader(WatchProtocol.NotificationXid, zxid, WatchProtocol.Success)
        );
        writer.WriteInt32(type);
        writer.WriteInt32(state);
        writer.WriteString(path);
        peer.Inject(bytes[..writer.WrittenCount]);
    }
}
