using NSubstitute;
using ZkLink.Configuration;
using ZkLink.Diagnostics;
using ZkLink.Protocol;
using ZkLink.Requests;
using ZkLink.Sessions;
using ZkLink.Watches;

namespace ZkLink.Protocol.Tests.Sessions;

public sealed class WatchManagerTests
{
    [Fact]
    [Trait("Acceptance", "T26")]
    public void ReconnectGapUsesTheNarrowSessionHandoff()
    {
        var gate = new object();
        using var options = ClientOptionsSnapshot.Capture(SessionFixture.Options(events: 8));
        var telemetry = new TelemetryState();
        var session = Substitute.For<IWatchSession>();
        session.Telemetry.Returns(telemetry);
        session.Session.Returns(
            new ZooKeeperSessionSnapshot(
                ZooKeeperSessionState.Connected,
                1,
                TimeSpan.FromSeconds(10),
                TimeSpan.FromSeconds(10),
                17,
                0,
                ZooKeeperCloseStatus.None,
                null
            )
        );
        var manager = new WatchManager(gate, options, new EventBudget(gate, 8, telemetry), session);
        var info = new ZooKeeperRequestInfo(ZooKeeperOperation.AddWatch, path: "/node");
        var intent = manager.Begin("/node", ZooKeeperWatchMode.Persistent, info);
        var pending = new PendingRequest(info, 0, 1, 0, default);
        var reply = new WireReply(new ReplyHeader(1, 1, 0), new EmptyResult());
        lock (gate)
        {
            intent.Replied(pending, reply);
            Assert.True(manager.Reconnected());
        }

        Assert.True(manager.TryRead(intent.Watch, out var value));
        Assert.NotNull(value);
        Assert.Equal(ZooKeeperWatchEventKind.ReconnectedWithGap, value.Kind);
        Assert.Equal(17, value.ConnectionGeneration);
        lock (gate)
        {
            manager.Finish(null);
        }
    }
}
