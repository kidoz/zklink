using System.Buffers.Binary;
using ZkLink.Configuration;
using ZkLink.Protocol;
using ZkLink.Requests;
using ZkLink.Sessions;

namespace ZkLink.Protocol.Tests.Sessions;

public sealed class RequestLifecycleTests : IDisposable
{
    private readonly ClientOptionsSnapshot _options = ClientOptionsSnapshot.Capture(
        SessionFixture.Options(inFlight: 2, queued: 2, bytes: 128)
    );
    private readonly ManualTimeProvider _time = new();
    private readonly RequestScheduler _scheduler;

    public RequestLifecycleTests()
    {
        _scheduler = new RequestScheduler(new object(), _options, _time, new AsyncPulse());
        _scheduler.Attach(1, new ZooKeeperEndpoint("peer", 2181));
    }

    public void Dispose()
    {
        _scheduler.Detach();
        _options.Dispose();
    }

    private Task<WireReply> Submit(string path = "/a", CancellationToken token = default) =>
        _scheduler.Submit(
            new PathRequest(9, path),
            new ZooKeeperRequestInfo(ZooKeeperOperation.Sync, path: path),
            token
        );

    private PendingRequest Take(bool finishWrite = true)
    {
        (var pending, var ping) = _scheduler.Take(1);
        Assert.False(ping);
        Assert.NotNull(pending);
        if (finishWrite)
        {
            _scheduler.WriteFinished(pending);
        }
        return pending;
    }

    private void Reply(PendingRequest pending, int error = 0) =>
        Assert.True(
            _scheduler.Complete(
                1,
                pending,
                new WireReply(
                    new ReplyHeader(pending.Info.RequestId!.Value, 20, error),
                    new PathResult("/a")
                )
            )
        );

    [Fact(Timeout = 10000)]
    [Trait("Acceptance", "T11")]
    public async Task QueuedCancellationSendsNothingAndReleasesItsBudget()
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken
        );
        var pending = Submit(token: stop.Token);
        Assert.True(_scheduler.OwnedBytes > 0);
        await stop.CancelAsync().ConfigureAwait(true);
        var error = await Assert
            .ThrowsAsync<ZooKeeperOperationCanceledException>(() => pending)
            .ConfigureAwait(true);
        Assert.Equal(RequestOutcome.NotSent, error.Outcome);
        Assert.True(pending.IsCanceled);
        Assert.Equal(0, _scheduler.OwnedBytes);
        Assert.Equal(0, _scheduler.PendingCount);
        Assert.Null(_scheduler.Take(1).Request);
    }

    [Fact(Timeout = 10000)]
    [Trait("Acceptance", "T11")]
    public async Task QueuedDeadlineIncludesTimeWaitingForTheWriter()
    {
        var pending = Submit(token: TestContext.Current.CancellationToken);
        _time.Advance(TimeSpan.FromMilliseconds(100));
        var error = await Assert
            .ThrowsAsync<ZooKeeperTimeoutException>(() => pending)
            .ConfigureAwait(true);
        Assert.Equal(RequestOutcome.NotSent, error.Outcome);
        Assert.Equal(0, _scheduler.OwnedBytes);
        Assert.Null(_scheduler.Take(1).Request);
    }

    [Fact(Timeout = 10000)]
    [Trait("Acceptance", "T12")]
    public async Task SentCancellationRetainsATombstoneAndDrainsTheLateReplyInOrder()
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken
        );
        var cancelled = Submit(token: stop.Token);
        var first = Take();
        var next = Submit("/next", TestContext.Current.CancellationToken);
        var second = Take();
        await stop.CancelAsync().ConfigureAwait(true);
        var error = await Assert
            .ThrowsAsync<ZooKeeperOperationCanceledException>(() => cancelled)
            .ConfigureAwait(true);
        Assert.Equal(RequestOutcome.Unknown, error.Outcome);
        Assert.True(cancelled.IsCanceled);
        Assert.Equal(first.Info.RequestId, error.Request.RequestId);
        Assert.Equal(2, _scheduler.PendingCount);
        Assert.Same(first, _scheduler.Expected(1, first.Info.RequestId!.Value));
        Reply(first);
        Assert.False(next.IsCompleted);
        Assert.Same(second, _scheduler.Expected(1, second.Info.RequestId!.Value));
        Reply(second);
        await next.ConfigureAwait(true);
        Assert.Equal(0, _scheduler.OwnedBytes);
    }

    [Fact(Timeout = 10000)]
    [Trait("Acceptance", "T12")]
    public async Task ReplyBeforeWriteCompletionCannotClearTheWritersBuffer()
    {
        var task = Submit(token: TestContext.Current.CancellationToken);
        var pending = Take(finishWrite: false);
        var packet = pending.Packet!;
        var expected = packet.ToArray();
        Reply(pending);
        await task.ConfigureAwait(true);
        Assert.Equal(expected, packet);
        Assert.True(_scheduler.OwnedBytes > 0);
        _scheduler.WriteFinished(pending);
        Assert.All(packet, value => Assert.Equal(0, value));
        Assert.Equal(0, _scheduler.OwnedBytes);
    }

    [Fact(Timeout = 10000)]
    [Trait("Acceptance", "T14")]
    public async Task InFlightCapacityIsRetainedByTimedOutRequestsUntilTheirReply()
    {
        var first = Submit(token: TestContext.Current.CancellationToken);
        var p1 = Take();
        var second = Submit(token: TestContext.Current.CancellationToken);
        var p2 = Take();
        _time.Advance(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAsync<ZooKeeperTimeoutException>(() => first).ConfigureAwait(true);
        await Assert.ThrowsAsync<ZooKeeperTimeoutException>(() => second).ConfigureAwait(true);
        var third = Submit(token: TestContext.Current.CancellationToken);
        Assert.Null(_scheduler.Take(1).Request);
        _scheduler.QueuePing();
        Assert.True(_scheduler.Take(1).Ping);
        Assert.True(_scheduler.ConsumePing(1));
        Reply(p1);
        var p3 = Take();
        Reply(p2);
        Reply(p3);
        await third.ConfigureAwait(true);
        Assert.Equal(0, _scheduler.PendingCount);
    }

    [Fact(Timeout = 10000)]
    [Trait("Acceptance", "T11")]
    public async Task BytePressureFailsWithoutConsumingAdditionalCapacity()
    {
        var first = Submit(
            new string('a', 90).Insert(0, "/"),
            TestContext.Current.CancellationToken
        );
        var bytes = _scheduler.OwnedBytes;
        var excess = Submit("/a-request-that-does-not-fit", TestContext.Current.CancellationToken);
        var failure = await Assert
            .ThrowsAsync<ZooKeeperBackpressureException>(() => excess)
            .ConfigureAwait(true);
        Assert.Equal(RequestOutcome.NotSent, failure.Outcome);
        Assert.Equal(bytes, _scheduler.OwnedBytes);
        Reply(Take());
        await first.ConfigureAwait(true);
    }

    [Fact(Timeout = 10000)]
    [Trait("Acceptance", "T11")]
    public async Task QueueCountPressureFailsImmediatelyWithoutAnAdmissionWaitList()
    {
        var first = Submit(token: TestContext.Current.CancellationToken);
        var second = Submit(token: TestContext.Current.CancellationToken);
        var excess = Submit(token: TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<ZooKeeperBackpressureException>(() => excess).ConfigureAwait(true);
        Assert.Equal(2, _scheduler.PendingCount);
        Reply(Take());
        Reply(Take());
        await Task.WhenAll(first, second).ConfigureAwait(true);
    }

    [Fact(Timeout = 10000)]
    [Trait("Acceptance", "T11")]
    public async Task CallerPayloadIsCapturedBeforeTheInvocationReturns()
    {
        byte[] data = [1, 2, 3];
        var task = _scheduler.Submit(
            new SetDataRequest("/a", data, -1),
            new ZooKeeperRequestInfo(ZooKeeperOperation.SetData, path: "/a"),
            TestContext.Current.CancellationToken
        );
        Array.Fill(data, (byte)9);
        var pending = Take();
        var reader = new JuteReader(pending.Packet!, _options.Limits);
        reader.ReadRequestHeader();
        Assert.Equal("/a", reader.ReadString());
        Assert.Equal(new byte[] { 1, 2, 3 }, reader.ReadBuffer());
        Assert.Null(Assert.IsType<SetDataRequest>(pending.Shape).Data);
        Reply(pending);
        await task.ConfigureAwait(true);
    }

    [Fact(Timeout = 10000)]
    [Trait("Acceptance", "T14")]
    public async Task PositiveXidsWrapAndSkipAllOutstandingIds()
    {
        var first = Submit(token: TestContext.Current.CancellationToken);
        var p1 = Take();
        Assert.Equal(1, p1.Info.RequestId);
        _scheduler.SetNextXidForTesting(int.MaxValue);
        var second = Submit(token: TestContext.Current.CancellationToken);
        var p2 = Take();
        Assert.Equal(int.MaxValue, p2.Info.RequestId);
        Reply(p1);
        await first.ConfigureAwait(true);
        // Keep xid 1 live while exercising a second wrap.
        _scheduler.SetNextXidForTesting(1);
        var third = Submit(token: TestContext.Current.CancellationToken);
        var p3 = Take();
        Assert.Equal(1, p3.Info.RequestId);
        Reply(p2);
        await second.ConfigureAwait(true);
        _scheduler.SetNextXidForTesting(1);
        var fourth = Submit(token: TestContext.Current.CancellationToken);
        var p4 = Take();
        Assert.Equal(2, p4.Info.RequestId);
        Assert.Equal(2, BinaryPrimitives.ReadInt32BigEndian(p4.Packet!));
        Reply(p3);
        Reply(p4);
        await Task.WhenAll(third, fourth).ConfigureAwait(true);
    }

    [Fact(Timeout = 10000)]
    [Trait("Acceptance", "T14")]
    public async Task WrongAndDuplicateXidsFaultTheCurrentFifoButOldGenerationsAreIgnored()
    {
        var task = Submit(token: TestContext.Current.CancellationToken);
        var pending = Take();
        var xid = pending.Info.RequestId!.Value;
        Assert.Same(pending, _scheduler.Expected(1, xid, out var shape));
        Assert.IsType<PathRequest>(shape);
        Assert.Throws<InvalidDataException>(() => _scheduler.Expected(1, xid + 1));
        Assert.Null(_scheduler.Expected(2, xid));
        Assert.False(
            _scheduler.Complete(
                2,
                pending,
                new WireReply(new ReplyHeader(xid, 1, 0), new EmptyResult())
            )
        );
        Assert.False(task.IsCompleted);
        Reply(pending);
        await task.ConfigureAwait(true);
        Assert.Throws<InvalidDataException>(() => _scheduler.Expected(1, xid));
    }

    [Fact(Timeout = 10000)]
    [Trait("Acceptance", "T13")]
    public async Task DisconnectSeparatesSentAndUnsentOutcomesAndNeverRequeuesEither()
    {
        var sent = Submit(token: TestContext.Current.CancellationToken);
        Take();
        var queued = Submit(token: TestContext.Current.CancellationToken);
        _scheduler.Detach();
        var uncertain = await Assert
            .ThrowsAsync<ZooKeeperException>(() => sent)
            .ConfigureAwait(true);
        var notSent = await Assert
            .ThrowsAsync<ZooKeeperException>(() => queued)
            .ConfigureAwait(true);
        Assert.Equal(RequestOutcome.Unknown, uncertain.Outcome);
        Assert.Equal(RequestOutcome.NotSent, notSent.Outcome);
        _scheduler.Attach(2, new ZooKeeperEndpoint("new", 2181));
        Assert.Null(_scheduler.Take(2).Request);
        Assert.Equal(0, _scheduler.OwnedBytes);
    }

    [Theory(Timeout = 10000)]
    [InlineData(-102, RequestOutcome.Rejected)]
    [InlineData(-103, RequestOutcome.Rejected)]
    [InlineData(-127, RequestOutcome.Rejected)]
    [InlineData(-999, RequestOutcome.Unknown)]
    [InlineData(-4, RequestOutcome.Unknown)]
    [InlineData(-118, RequestOutcome.Unknown)]
    public async Task RejectionsAreDistinguishedFromUnknownErrors(int code, RequestOutcome outcome)
    {
        var task = Submit(token: TestContext.Current.CancellationToken);
        var pending = Take();
        Reply(pending, code);
        var error = await Assert.ThrowsAsync<ZooKeeperException>(() => task).ConfigureAwait(true);
        Assert.Equal(code, error.ErrorCode);
        Assert.Equal(outcome, error.Outcome);
    }

    [Fact(Timeout = 10000)]
    [Trait("Acceptance", "T14")]
    public async Task CloseHasReservedCapacityAndKeepsSentRepliesOrdered()
    {
        var application = Submit(token: TestContext.Current.CancellationToken);
        var app = Take();
        var close = _scheduler.EnqueueClose();
        Assert.Same(close, Take());
        Assert.Equal(-11, close.Info.Opcode);
        await Assert.ThrowsAsync<ZooKeeperException>(() => application).ConfigureAwait(true);
        Assert.Throws<InvalidDataException>(() =>
            _scheduler.Expected(1, close.Info.RequestId!.Value)
        );
        Reply(app);
        Assert.Same(close, _scheduler.Expected(1, close.Info.RequestId!.Value));
        Reply(close);
        await close.Task.ConfigureAwait(true);
        Assert.Equal(0, _scheduler.OwnedBytes);
    }

    [Fact(Timeout = 10000)]
    [Trait("Acceptance", "T14")]
    public async Task PingAcknowledgmentsDoNotExtendTheOldestUnansweredRequestBudget()
    {
        var task = Submit(token: TestContext.Current.CancellationToken);
        var pending = Take();
        _time.Advance(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAsync<ZooKeeperTimeoutException>(() => task).ConfigureAwait(true);
        _time.Advance(TimeSpan.FromMilliseconds(600));
        _scheduler.QueuePing();
        Assert.True(_scheduler.Take(1).Ping);
        _scheduler.ConsumePing(1);
        Assert.Equal(TimeSpan.Zero, _scheduler.OldestRemaining(TimeSpan.FromMilliseconds(600)));
        Reply(pending);
        Assert.Null(_scheduler.OldestRemaining(TimeSpan.FromMilliseconds(600)));
    }
}
