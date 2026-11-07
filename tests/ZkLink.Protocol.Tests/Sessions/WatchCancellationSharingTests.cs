using ZkLink.Protocol;
using ZkLink.Watches;

namespace ZkLink.Protocol.Tests.Sessions;

public sealed class WatchCancellationSharingTests
{
    private const int TestTimeoutMilliseconds = 10_000;
    private const int ReplyBufferBytes = 4096;
    private const long ReplyZxid = 12;
    private const long EventZxid = 13;
    private const string ClientPath = "/node";
    private const string ServerPath = "/app/node";

    public static TheoryData<
        ZooKeeperCompatibilityProfile,
        ZooKeeperWatchMode,
        ZooKeeperWatchMode,
        bool,
        bool
    > Cases
    {
        get
        {
            var cases =
                new TheoryData<
                    ZooKeeperCompatibilityProfile,
                    ZooKeeperWatchMode,
                    ZooKeeperWatchMode,
                    bool,
                    bool
                >();
            (ZooKeeperWatchMode Older, ZooKeeperWatchMode Pending, bool Missing)[] pairs =
            [
                (ZooKeeperWatchMode.OneShotExists, ZooKeeperWatchMode.OneShotExists, false),
                (ZooKeeperWatchMode.OneShotExists, ZooKeeperWatchMode.OneShotExists, true),
                (ZooKeeperWatchMode.OneShotData, ZooKeeperWatchMode.OneShotData, false),
                (ZooKeeperWatchMode.OneShotChildren, ZooKeeperWatchMode.OneShotChildren, false),
                (ZooKeeperWatchMode.OneShotData, ZooKeeperWatchMode.OneShotExists, false),
                (ZooKeeperWatchMode.OneShotExists, ZooKeeperWatchMode.OneShotData, false),
                (ZooKeeperWatchMode.Persistent, ZooKeeperWatchMode.Persistent, false),
                (
                    ZooKeeperWatchMode.PersistentRecursive,
                    ZooKeeperWatchMode.PersistentRecursive,
                    false
                ),
            ];
            foreach (
                var profile in new[]
                {
                    ZooKeeperCompatibilityProfile.ZooKeeper38,
                    ZooKeeperCompatibilityProfile.ZooKeeper39,
                }
            )
            {
                foreach (var pair in pairs)
                {
                    foreach (bool lateFailure in new[] { false, true })
                    {
                        cases.Add(profile, pair.Older, pair.Pending, pair.Missing, lateFailure);
                    }
                }
            }
            return cases;
        }
    }

    [Theory(Timeout = TestTimeoutMilliseconds)]
    [MemberData(nameof(Cases))]
    [Trait("Acceptance", "T12")]
    [Trait("Acceptance", "T28")]
    [Trait("Acceptance", "T29")]
    public async Task CanceledSharerPreservesOlderWatchAfterLateSuccessOrFailure(
        ZooKeeperCompatibilityProfile profile,
        ZooKeeperWatchMode olderMode,
        ZooKeeperWatchMode pendingMode,
        bool missing,
        bool lateFailure
    )
    {
        var token = TestContext.Current.CancellationToken;
        var fixture = new SessionFixture();
        await using var fixtureLifetime = fixture.ConfigureAwait(true);
        using var peer = new SessionPeer { ReplyApplications = false };
        var client = await fixture
            .ConnectAsync(
                peer,
                SessionFixture.Options(connection: "peer:2181/app", profile: profile),
                token
            )
            .ConfigureAwait(true);
        await SessionTestSupport.NextAsync(peer).ConfigureAwait(true);
        long generation = client.Session.ConnectionGeneration;
        var first = RegisterAsync(client, olderMode, token);
        Reply(
            peer,
            await SessionTestSupport.NextAsync(peer).ConfigureAwait(true),
            olderMode,
            missing
        );
        var survivor = await first.ConfigureAwait(true);
        await using var survivorLifetime = survivor.ConfigureAwait(true);

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        var canceled = RegisterAsync(client, pendingMode, stop.Token);
        var request = await SessionTestSupport.NextAsync(peer).ConfigureAwait(true);
        await stop.CancelAsync().ConfigureAwait(true);
        var error = await Assert
            .ThrowsAsync<ZooKeeperOperationCanceledException>(() => canceled)
            .ConfigureAwait(true);
        Assert.Equal(RequestOutcome.Unknown, error.Outcome);
        Assert.Equal(request.Xid, error.Request.RequestId);
        Assert.Equal(request.Opcode, error.Request.Opcode);
        Assert.Equal(ClientPath, error.Request.Path);
        Assert.Equal(1, client.Manager.Scheduler.PendingCount);
        if (lateFailure)
        {
            peer.Reply(request, (int)ZooKeeperErrorCode.NoAuth);
        }
        else
        {
            Reply(peer, request, pendingMode, missing);
        }
        await BarrierAsync(client, peer, token).ConfigureAwait(true);
        await SessionTestSupport.WaitForBytesReleasedAsync(client).ConfigureAwait(true);
        Assert.Equal(1, client.Manager.WatchCount);
        Assert.Equal(0, peer.Requests.Count);
        Assert.Equal(0, client.Manager.Scheduler.PendingCount);
        Assert.Equal(0, client.Manager.Scheduler.OwnedBytes);

        var kind =
            missing ? ZooKeeperWatchEventKind.NodeCreated
            : olderMode == ZooKeeperWatchMode.OneShotChildren
                ? ZooKeeperWatchEventKind.NodeChildrenChanged
            : ZooKeeperWatchEventKind.NodeDataChanged;
        var bytes = new byte[ReplyBufferBytes];
        var writer = new JuteWriter(bytes, ProtocolFixtures.Limits);
        writer.WriteReplyHeader(
            new ReplyHeader(WatchProtocol.NotificationXid, EventZxid, WatchProtocol.Success)
        );
        writer.WriteInt32((int)kind);
        writer.WriteInt32(WatchProtocol.ServerConnected);
        writer.WriteString(ServerPath);
        peer.Inject(bytes[..writer.WrittenCount]);
        await BarrierAsync(client, peer, token).ConfigureAwait(true);
        var events = survivor.ReadEventsAsync(token).GetAsyncEnumerator(token);
        await using var eventsLifetime = events.ConfigureAwait(true);
        Assert.True(await events.MoveNextAsync().ConfigureAwait(true));
        Assert.Equal(kind, events.Current.Kind);
        Assert.Equal(survivor.RegistrationId, events.Current.RegistrationId);
        Assert.Equal(ClientPath, events.Current.Path);
        Assert.Equal(EventZxid, events.Current.Zxid);
        Assert.Equal(generation, events.Current.ConnectionGeneration);
        if (olderMode is ZooKeeperWatchMode.Persistent or ZooKeeperWatchMode.PersistentRecursive)
        {
            Assert.Equal(1, client.Manager.WatchCount);
            var remove = survivor.RemoveAsync(WatchRemovalMode.ServerConfirmed, token);
            var removal = await SessionTestSupport.NextAsync(peer).ConfigureAwait(true);
            Assert.Equal((int)ZooKeeperOperation.RemoveWatches, removal.Opcode);
            var reader = new JuteReader(removal.Bytes, ProtocolFixtures.Limits);
            reader.ReadRequestHeader();
            Assert.Equal(ServerPath, reader.ReadString());
            int expectedCategory =
                profile == ZooKeeperCompatibilityProfile.ZooKeeper38 ? WatchProtocol.Any
                : olderMode == ZooKeeperWatchMode.Persistent ? WatchProtocol.Persistent
                : WatchProtocol.PersistentRecursive;
            Assert.Equal(expectedCategory, reader.ReadInt32());
            reader.EnsureEnd();
            peer.Reply(removal);
            Assert.Equal(
                WatchRemovalStatus.ServerConfirmed,
                (await remove.ConfigureAwait(true)).Status
            );
        }
        Assert.False(await events.MoveNextAsync().ConfigureAwait(true));
        await BarrierAsync(client, peer, token).ConfigureAwait(true);
        await SessionTestSupport.WaitForBytesReleasedAsync(client).ConfigureAwait(true);
        Assert.Equal(ZooKeeperSessionState.Connected, client.Session.State);
        Assert.Equal(generation, client.Session.ConnectionGeneration);
        Assert.Equal(0, client.Manager.WatchCount);
        Assert.Equal(0, client.Manager.Scheduler.PendingCount);
        Assert.Equal(0, client.Manager.Scheduler.OwnedBytes);
        Assert.Equal(0, peer.Requests.Count);
    }

    private static async Task<ZooKeeperWatch> RegisterAsync(
        ZooKeeperClient client,
        ZooKeeperWatchMode mode,
        CancellationToken token
    ) =>
        mode switch
        {
            ZooKeeperWatchMode.OneShotExists => (
                await client.ExistsAndWatchAsync(ClientPath, token).ConfigureAwait(false)
            ).Watch,
            ZooKeeperWatchMode.OneShotData => (
                await client.GetDataAndWatchAsync(ClientPath, token).ConfigureAwait(false)
            ).Watch,
            ZooKeeperWatchMode.OneShotChildren => (
                await client.GetChildrenAndWatchAsync(ClientPath, token).ConfigureAwait(false)
            ).Watch,
            ZooKeeperWatchMode.Persistent => await client
                .AddWatchAsync(ClientPath, PersistentWatchMode.Persistent, token)
                .ConfigureAwait(false),
            ZooKeeperWatchMode.PersistentRecursive => await client
                .AddWatchAsync(ClientPath, PersistentWatchMode.PersistentRecursive, token)
                .ConfigureAwait(false),
            _ => throw new ArgumentOutOfRangeException(nameof(mode)),
        };

    private static void Reply(
        SessionPeer peer,
        ReceivedPacket packet,
        ZooKeeperWatchMode mode,
        bool missing
    )
    {
        if (mode is ZooKeeperWatchMode.Persistent or ZooKeeperWatchMode.PersistentRecursive)
        {
            peer.Reply(packet);
            return;
        }
        if (missing)
        {
            peer.Reply(packet, (int)ZooKeeperErrorCode.NoNode);
            return;
        }
        var bytes = new byte[ReplyBufferBytes];
        var writer = new JuteWriter(bytes, ProtocolFixtures.Limits);
        writer.WriteReplyHeader(new ReplyHeader(packet.Xid, ReplyZxid, WatchProtocol.Success));
        if (mode == ZooKeeperWatchMode.OneShotData)
        {
            writer.WriteNullBuffer();
        }
        if (mode == ZooKeeperWatchMode.OneShotChildren)
        {
            writer.WriteInt32(0);
        }
        writer.WriteStat(ProtocolFixtures.Stat);
        peer.Inject(bytes[..writer.WrittenCount]);
    }

    private static async Task BarrierAsync(
        ZooKeeperClient client,
        SessionPeer peer,
        CancellationToken token
    )
    {
        var barrier = client.SyncAsync("/barrier", token);
        var packet = await SessionTestSupport.NextAsync(peer).ConfigureAwait(false);
        Assert.Equal((int)ZooKeeperOperation.Sync, packet.Opcode);
        peer.Reply(packet);
        await barrier.ConfigureAwait(false);
    }
}
