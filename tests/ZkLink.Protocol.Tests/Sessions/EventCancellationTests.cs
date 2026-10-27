using NSubstitute;
using ZkLink.Configuration;
using ZkLink.Diagnostics;
using ZkLink.Protocol;
using ZkLink.Requests;
using ZkLink.Sessions;
using ZkLink.Watches;

namespace ZkLink.Protocol.Tests.Sessions;

public sealed class EventCancellationTests
{
    [Fact(Timeout = 10000)]
    [Trait("Acceptance", "T31")]
    public async Task StateTakenByACancellingReaderIsDelivered()
    {
        var gate = new object();
        var initial = Snapshot();
        var hub = new StateHub(gate, new EventBudget(gate, 4), initial);
        await using var subscription = hub.Subscribe()!;
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken
        );
        await using var reader = subscription
            .ReadStatesAsync(stop.Token)
            .GetAsyncEnumerator(stop.Token);

        await CancelDuringReadAsync(gate, reader, stop).ConfigureAwait(true);
        Assert.Same(initial, reader.Current);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            reader.MoveNextAsync().AsTask()
        );
    }

    [Fact(Timeout = 10000)]
    [Trait("Acceptance", "T24")]
    [Trait("Acceptance", "T31")]
    public async Task OneShotEventTakenByACancellingReaderIsDelivered()
    {
        var gate = new object();
        using var options = ClientOptionsSnapshot.Capture(SessionFixture.Options());
        var session = Substitute.For<IWatchSession>();
        session.Session.Returns(Snapshot());
        session.Telemetry.Returns(new TelemetryState());
        var manager = new WatchManager(gate, options, new EventBudget(gate, 4), session);
        var info = new ZooKeeperRequestInfo(ZooKeeperOperation.Exists, path: "/node");
        var intent = manager.Begin("/node", ZooKeeperWatchMode.OneShotExists, info);
        lock (gate)
        {
            intent.Replied(
                new PendingRequest(info, 0, 1, 0, default),
                new WireReply(
                    new ReplyHeader(1, 1, (int)ZooKeeperErrorCode.NoNode),
                    new EmptyResult()
                )
            );
            Assert.True(
                manager.Dispatch(
                    new WireNotification((int)ZooKeeperWatchEventKind.NodeCreated, 3, "/node", 2)
                )
            );
        }
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken
        );
        await using var reader = intent
            .Watch.ReadEventsAsync(stop.Token)
            .GetAsyncEnumerator(stop.Token);

        await CancelDuringReadAsync(gate, reader, stop).ConfigureAwait(true);
        Assert.Equal(ZooKeeperWatchEventKind.NodeCreated, reader.Current.Kind);
        Assert.Equal("/node", reader.Current.Path);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            reader.MoveNextAsync().AsTask()
        );
        Assert.Equal(0, manager.Count);
    }

    private static async Task CancelDuringReadAsync<T>(
        object gate,
        IAsyncEnumerator<T> reader,
        CancellationTokenSource stop
    )
    {
        var read = new TaskCompletionSource<Task<bool>>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        // The queue is already readable. The only blocking point on this dedicated
        // thread is the owner's dequeue gate, after the reader's pre-read check.
        var thread = new Thread(() =>
        {
            try
            {
                read.TrySetResult(reader.MoveNextAsync().AsTask());
            }
            catch (Exception error)
                when (error is OperationCanceledException or InvalidOperationException)
            {
                read.TrySetException(error);
            }
        })
        {
            IsBackground = true,
        };
        Task cancellation;
        lock (gate)
        {
            thread.Start();
            Assert.True(
                SpinWait.SpinUntil(
                    () => (thread.ThreadState & ThreadState.WaitSleepJoin) != 0,
                    SessionTestSupport.Guard
                )
            );
            cancellation = stop.CancelAsync();
        }
        await cancellation.ConfigureAwait(false);
        var move = await read
            .Task.WaitAsync(SessionTestSupport.Guard, TestContext.Current.CancellationToken)
            .ConfigureAwait(false);
        Assert.True(await move.ConfigureAwait(false));
    }

    private static ZooKeeperSessionSnapshot Snapshot() =>
        new(
            ZooKeeperSessionState.Connected,
            1,
            TimeSpan.FromSeconds(10),
            TimeSpan.FromSeconds(10),
            1,
            0,
            ZooKeeperCloseStatus.None,
            null
        );
}
