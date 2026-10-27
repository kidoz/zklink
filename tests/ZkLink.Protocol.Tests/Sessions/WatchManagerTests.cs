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

    [Fact(Timeout = 10000)]
    [Trait("Acceptance", "T29")]
    public async Task OverlappingServerRemovalLeavesTheNewHandleUsableAndRetryable()
    {
        using var fixture = new RemovalFixture();
        var removal = new TaskCompletionSource<WireReply>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        IRequestObserver? observer = null;
        fixture
            .Session.SubmitWatchRemoval(
                Arg.Any<WatchRequest>(),
                Arg.Any<IRequestObserver>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(call =>
            {
                observer = call.Arg<IRequestObserver>();
                return removal.Task;
            });
        var first = fixture.Register();
        var removing = first.RemoveAsync(
            WatchRemovalMode.ServerConfirmed,
            TestContext.Current.CancellationToken
        );
        var second = fixture.Register();

        await Assert
            .ThrowsAsync<ZooKeeperBackpressureException>(() =>
                second.RemoveAsync(
                    WatchRemovalMode.ServerConfirmed,
                    TestContext.Current.CancellationToken
                )
            )
            .ConfigureAwait(true);
        Assert.True(second.Active);
        Assert.False(second.Suppressed);
        Assert.Equal(1, fixture.Manager.Count);
        lock (fixture.Gate)
        {
            Assert.True(
                fixture.Manager.Dispatch(
                    new WireNotification(
                        (int)ZooKeeperWatchEventKind.NodeDataChanged,
                        3,
                        "/node",
                        2
                    )
                )
            );
        }
        Assert.True(fixture.Manager.TryRead(second, out var value));
        Assert.Equal(ZooKeeperWatchEventKind.NodeDataChanged, value!.Kind);
        lock (fixture.Gate)
        {
            observer!.Replied(fixture.Pending, fixture.Reply);
            removal.SetResult(fixture.Reply);
        }
        await removing.ConfigureAwait(true);
        removal = new TaskCompletionSource<WireReply>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var retry = second.RemoveAsync(
            WatchRemovalMode.ServerConfirmed,
            TestContext.Current.CancellationToken
        );
        Assert.False(retry.IsCompleted);
        lock (fixture.Gate)
        {
            observer!.Replied(fixture.Pending, fixture.Reply);
            removal.SetResult(fixture.Reply);
        }
        await retry.ConfigureAwait(true);
        Assert.Equal(0, fixture.Manager.Count);
    }

    [Theory(Timeout = 10000)]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Acceptance", "T29")]
    public async Task InlineCleanupCancellationDoesNotLatchAnAbsentRequest(bool timeout)
    {
        using var fixture = new RemovalFixture();
        var attempts = 0;
        var outstanding = new TaskCompletionSource<WireReply>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        fixture
            .Session.TryWatchCleanup(
                Arg.Any<WatchRequest>(),
                Arg.Any<IRequestObserver>(),
                out Arg.Any<Task<WireReply>?>()
            )
            .Returns(call =>
            {
                attempts++;
                if (attempts == 1)
                {
                    call.Arg<IRequestObserver>().Canceled(fixture.Pending);
                    Exception error = timeout
                        ? new ZooKeeperTimeoutException(fixture.Info, RequestOutcome.NotSent)
                        : new ZooKeeperOperationCanceledException(
                            fixture.Info,
                            RequestOutcome.NotSent,
                            new CancellationToken(true)
                        );
                    call[2] = Task.FromException<WireReply>(error);
                }
                else
                {
                    call[2] = outstanding.Task;
                }
                return true;
            });
        await fixture.Register().DisposeAsync().ConfigureAwait(true);
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        lock (fixture.Gate)
        {
            fixture.Manager.PrepareCleanup();
            Assert.Equal(1, attempts);
            fixture.Manager.PrepareCleanup();
            Assert.Equal(2, attempts);
            fixture.Manager.PrepareCleanup();
            Assert.Equal(2, attempts);
        }
        outstanding.SetResult(fixture.Reply);
    }

    private sealed class RemovalFixture : IDisposable
    {
        internal object Gate { get; } = new();
        private readonly ClientOptionsSnapshot _options = ClientOptionsSnapshot.Capture(
            SessionFixture.Options(watches: 2)
        );
        internal IWatchSession Session { get; } = Substitute.For<IWatchSession>();
        internal WatchManager Manager { get; }
        internal ZooKeeperRequestInfo Info { get; } =
            new(ZooKeeperOperation.RemoveWatches, path: "/node");
        internal PendingRequest Pending { get; }
        internal WireReply Reply { get; } = new(new ReplyHeader(1, 1, 0), new EmptyResult());

        internal RemovalFixture()
        {
            var telemetry = new TelemetryState();
            Session.Telemetry.Returns(telemetry);
            Session.Session.Returns(
                new ZooKeeperSessionSnapshot(
                    ZooKeeperSessionState.Connected,
                    1,
                    TimeSpan.FromSeconds(10),
                    TimeSpan.FromSeconds(10),
                    1,
                    0,
                    ZooKeeperCloseStatus.None,
                    null
                )
            );
            Manager = new WatchManager(
                Gate,
                _options,
                new EventBudget(Gate, 8, telemetry),
                Session
            );
            Pending = new PendingRequest(Info, 0, 1, 0, default);
        }

        internal ZooKeeperWatch Register()
        {
            var intent = Manager.Begin(
                "/node",
                ZooKeeperWatchMode.Persistent,
                new ZooKeeperRequestInfo(ZooKeeperOperation.AddWatch, path: "/node")
            );
            lock (Gate)
            {
                intent.Replied(Pending, Reply);
            }
            return intent.Watch;
        }

        public void Dispose()
        {
            lock (Gate)
            {
                Manager.Finish(null);
            }
            _options.Dispose();
        }
    }
}
