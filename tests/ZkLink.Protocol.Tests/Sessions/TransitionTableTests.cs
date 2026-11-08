using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using NSubstitute;
using ZkLink.Configuration;
using ZkLink.Diagnostics;
using ZkLink.Protocol;
using ZkLink.Requests;
using ZkLink.Sessions;
using ZkLink.Watches;

namespace ZkLink.Protocol.Tests.Sessions;

public sealed class RequestTransitionTableTests : IDisposable
{
    private readonly ClientOptionsSnapshot _options = ClientOptionsSnapshot.Capture(
        SessionFixture.Options(inFlight: 8, queued: 8, bytes: 1024 * 1024)
    );
    private readonly ManualTimeProvider _time = new();
    private readonly RequestScheduler _scheduler;

    public RequestTransitionTableTests()
    {
        _scheduler = new RequestScheduler(new object(), _options, _time, new AsyncPulse());
        _scheduler.Attach(1, new ZooKeeperEndpoint("peer", 2181));
    }

    public void Dispose()
    {
        _scheduler.Detach();
        _options.Dispose();
    }

    [Theory(Timeout = 10000)]
    [InlineData(true)]
    [InlineData(false)]
    [Trait("Acceptance", "T11")]
    public async Task EncodeWindowFinishCompletesBeforeTheReservationIsReleased(bool detach)
    {
        var admission = StartAdmittedSubmit(failEncode: false);
        PendingRequest? pending = null;
        try
        {
            pending = await AdmittedRequestAsync(
                admission,
                TestContext.Current.CancellationToken
            ).ConfigureAwait(true);
            AssertRequest(pending, preparing: true, released: false);
            Assert.True(_scheduler.OwnedBytes > 0);
            PendingRequest? close = null;
            if (detach)
            {
                _scheduler.Detach();
            }
            else
            {
                close = _scheduler.EnqueueClose();
            }

            AssertRequest(pending, preparing: true, completed: true, released: false);
            if (close is not null)
            {
                AssertRequest(close, prepared: true);
                Assert.Equal((int)ZooKeeperOperation.CloseSession, close.Info.Opcode);
            }
        }
        finally
        {
            admission.Release.TrySetResult();
        }

        var error = await Assert
            .ThrowsAsync<ZooKeeperException>(() => admission.Submit)
            .ConfigureAwait(true);
        Assert.Equal(RequestOutcome.NotSent, error.Outcome);
        Assert.Null(error.Request.RequestId);
        AssertRequest(pending!, preparing: false, completed: true, released: true);
        Assert.Equal(0, _scheduler.OwnedBytes);
    }

    [Fact(Timeout = 10000)]
    [Trait("Acceptance", "T11")]
    public async Task EncodeFailureAfterReservationReleasesBytesWithoutPreparing()
    {
        var admission = StartAdmittedSubmit(failEncode: true);
        PendingRequest? pending = null;
        try
        {
            pending = await AdmittedRequestAsync(
                admission,
                TestContext.Current.CancellationToken
            ).ConfigureAwait(true);
            AssertRequest(pending, preparing: true, released: false);
            Assert.True(_scheduler.OwnedBytes > 0);
        }
        finally
        {
            admission.Release.TrySetResult();
        }

        await Assert
            .ThrowsAsync<InvalidDataException>(() => admission.Submit)
            .ConfigureAwait(true);
        AssertRequest(pending!, preparing: false, completed: true, released: true);
        Assert.Equal(0, _scheduler.OwnedBytes);
        Assert.Equal(0, _scheduler.PendingCount);
    }

    [Fact(Timeout = 10000)]
    [Trait("Acceptance", "T13")]
    public async Task DetachDuringWriteCompletesWithoutReleaseUntilTheWriteFinishes()
    {
        var task = Submit("/a", TestContext.Current.CancellationToken);
        var release = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var (pending, writing) = _scheduler.WriteNext(
            1,
            _ => release.Task,
            TestContext.Current.CancellationToken
        );
        Assert.NotNull(pending);
        AssertRequest(pending, prepared: true, sent: true, writing: true);
        _scheduler.Detach();
        AssertRequest(pending, prepared: true, sent: true, writing: true, completed: true);
        Assert.Equal(0, Tombstones(_scheduler));
        release.TrySetResult();
        await writing!.ConfigureAwait(true);
        _scheduler.WriteFinished(pending);
        AssertRequest(
            pending,
            prepared: true,
            sent: true,
            completed: true,
            released: true
        );
        Assert.Equal(0, _scheduler.OwnedBytes);
        var error = await Assert.ThrowsAsync<ZooKeeperException>(() => task).ConfigureAwait(true);
        Assert.Equal(RequestOutcome.Unknown, error.Outcome);
    }

    [Fact(Timeout = 10000)]
    [Trait("Acceptance", "T12")]
    public async Task DetachDuringTombstonedWriteKeepsTheTombstoneThroughRelease()
    {
        using var stop = new CancellationTokenSource();
        var task = Submit("/a", stop.Token);
        var (pending, writing) = _scheduler.WriteNext(
            1,
            _ => stop.CancelAsync(),
            TestContext.Current.CancellationToken
        );
        Assert.NotNull(pending);
        await writing!.ConfigureAwait(true);
        AssertRequest(pending, prepared: true, sent: true, writing: true, tombstone: true);
        Assert.Equal(1, Tombstones(_scheduler));
        _scheduler.Detach();
        AssertRequest(
            pending,
            prepared: true,
            sent: true,
            writing: true,
            completed: true,
            tombstone: true
        );
        Assert.Equal(0, Tombstones(_scheduler));
        _scheduler.WriteFinished(pending);
        AssertRequest(
            pending,
            prepared: true,
            sent: true,
            completed: true,
            released: true,
            tombstone: true
        );
        Assert.Equal(0, Tombstones(_scheduler));
        Assert.Equal(0, _scheduler.OwnedBytes);
        var error = await Assert
            .ThrowsAsync<ZooKeeperOperationCanceledException>(() => task)
            .ConfigureAwait(true);
        Assert.Equal(RequestOutcome.Unknown, error.Outcome);
    }

    [Fact(Timeout = 10000)]
    [Trait("Acceptance", "T12")]
    public async Task DetachAfterAFinishedTombstonedWriteKeepsTheFlagAndClearsTheCounter()
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken
        );
        var task = Submit("/a", stop.Token);
        var pending = Take();
        await stop.CancelAsync().ConfigureAwait(true);
        AssertRequest(pending, prepared: true, sent: true, tombstone: true);
        Assert.Equal(1, Tombstones(_scheduler));
        _scheduler.Detach();
        AssertRequest(
            pending,
            prepared: true,
            sent: true,
            completed: true,
            released: true,
            tombstone: true
        );
        Assert.Equal(0, Tombstones(_scheduler));
        Assert.Equal(0, _scheduler.OwnedBytes);
        var error = await Assert
            .ThrowsAsync<ZooKeeperOperationCanceledException>(() => task)
            .ConfigureAwait(true);
        Assert.Equal(RequestOutcome.Unknown, error.Outcome);
    }

    [Fact(Timeout = 10000)]
    [Trait("Acceptance", "T14")]
    public async Task EnqueueCloseDuringWriteTombstonesWithoutCompletingTheRequest()
    {
        var task = Submit("/a", TestContext.Current.CancellationToken);
        var release = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var (pending, writing) = _scheduler.WriteNext(
            1,
            _ => release.Task,
            TestContext.Current.CancellationToken
        );
        var close = _scheduler.EnqueueClose();
        Assert.NotNull(pending);
        AssertRequest(pending, prepared: true, sent: true, writing: true, tombstone: true);
        Assert.False(pending.Completed);
        Assert.Equal(1, Tombstones(_scheduler));
        AssertRequest(close, prepared: true);
        var error = await Assert.ThrowsAsync<ZooKeeperException>(() => task).ConfigureAwait(true);
        Assert.Equal(RequestOutcome.Unknown, error.Outcome);
        Assert.Equal(pending.Info.RequestId, error.Request.RequestId);
        release.TrySetResult();
        await writing!.ConfigureAwait(true);
        _scheduler.WriteFinished(pending);
        AssertRequest(pending, prepared: true, sent: true, writing: false, tombstone: true);
        Assert.False(pending.Completed);
        Assert.False(pending.Released);
        Assert.True(_scheduler.OwnedBytes > 0);
        Reply(pending);
        AssertRequest(
            pending,
            prepared: true,
            sent: true,
            completed: true,
            released: true
        );
        Assert.Equal(0, Tombstones(_scheduler));
    }

    [Fact(Timeout = 10000)]
    [Trait("Acceptance", "T14")]
    public async Task StoppedAdmissionStillSendsAQueuedPing()
    {
        _scheduler.QueuePing();
        _scheduler.StopAdmission();
        Assert.True(_scheduler.PingPending);
        var rejected = Submit("/a", TestContext.Current.CancellationToken);
        var error = await Assert
            .ThrowsAsync<ZooKeeperException>(() => rejected)
            .ConfigureAwait(true);
        Assert.Equal(RequestOutcome.NotSent, error.Outcome);
        var taken = _scheduler.Take(1);
        Assert.True(taken.Ping);
        Assert.Null(taken.Request);
        Assert.True(_scheduler.PingPending);
        Assert.True(_scheduler.ConsumePing(1));
        Assert.False(_scheduler.PingPending);
    }

    [Fact(Timeout = 10000)]
    [Trait("Acceptance", "T13")]
    public void AttachWithoutDetachPreservesQueueMaintenanceXidsAndBytes()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        var sent = Submit("/sent", TestContext.Current.CancellationToken);
        var taken = Take(finishWrite: false);
        var queued = Submit("/queued", TestContext.Current.CancellationToken);
        var maintenance = _scheduler.Submit(
            new WatchRequest(ZooKeeperOperation.RemoveWatches, "/a", WatchProtocol.Any),
            new ZooKeeperRequestInfo(ZooKeeperOperation.RemoveWatches, path: "/a"),
            TestContext.Current.CancellationToken,
            watchOperation: true,
            maintenance: true
        );
        var bytes = _scheduler.OwnedBytes;
        var pending = _scheduler.PendingCount;
        Assert.False(_scheduler.MaintenanceAvailable);
        Assert.Equal(3, pending);

        _scheduler.Attach(2, new ZooKeeperEndpoint("next", 2181));

        Assert.Equal(pending, _scheduler.PendingCount);
        Assert.Equal(bytes, _scheduler.OwnedBytes);
        Assert.False(_scheduler.MaintenanceAvailable);
        Assert.False(sent.IsCompleted);
        Assert.False(queued.IsCompleted);
        Assert.False(maintenance.IsCompleted);
        _scheduler.SetNextXidForTesting(1);
        var first = Take(2);
        Assert.Equal(ZooKeeperOperation.RemoveWatches, first.Info.Operation);
        Assert.Equal(2, first.Info.RequestId);
        var second = Take(2);
        Assert.Equal("/queued", second.Info.Path);
        Assert.Equal(3, second.Info.RequestId);
        Assert.Equal(1, taken.Info.RequestId);
        Assert.True(taken.Writing);
        _scheduler.WriteFinished(taken);
        _scheduler.WriteFinished(first);
        _scheduler.WriteFinished(second);
    }

    private async Task<PendingRequest> AdmittedRequestAsync(
        AdmittedSubmit admission,
        CancellationToken cancellationToken
    )
    {
        var finished = await Task
            .WhenAny(admission.Entered.Task, admission.Submit)
            .WaitAsync(SessionTestSupport.Guard, cancellationToken)
            .ConfigureAwait(true);
        if (finished == admission.Submit)
        {
            try
            {
                await admission.Submit.ConfigureAwait(true);
            }
            catch (Exception error)
            {
                throw new InvalidOperationException(
                    "Encode did not pause. Walks=" + admission.Members.Walks,
                    error
                );
            }
            throw new InvalidOperationException(
                "Encode did not pause. Walks=" + admission.Members.Walks
            );
        }
        return Queued(_scheduler).First!.Value;
    }

    private AdmittedSubmit StartAdmittedSubmit(bool failEncode)
    {
        // Measure walks members twice before the reservation lock. The third walk is
        // ToServer, after that lock has been released and before Prepared is set.
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var members = new ThirdWalkGate(
            [new VersionRequest(ZooKeeperOperation.Delete, "/a", -1)],
            entered,
            release,
            failEncode,
            TestContext.Current.CancellationToken
        );
        var submit = Task.Run(
            () =>
                _scheduler.Submit(
                    new MultiRequest(false, members),
                    new ZooKeeperRequestInfo(ZooKeeperOperation.Multi),
                    TestContext.Current.CancellationToken
                ),
            TestContext.Current.CancellationToken
        );
        return new AdmittedSubmit(submit, entered, release, members);
    }

    private Task<WireReply> Submit(string path, CancellationToken token) =>
        _scheduler.Submit(
            new PathRequest(ZooKeeperOperation.Sync, path),
            new ZooKeeperRequestInfo(ZooKeeperOperation.Sync, path: path),
            token
        );

    private PendingRequest Take(long generation = 1, bool finishWrite = true)
    {
        (var pending, var ping) = _scheduler.Take(generation);
        Assert.False(ping);
        Assert.NotNull(pending);
        if (finishWrite)
        {
            _scheduler.WriteFinished(pending);
        }
        return pending;
    }

    private void Reply(PendingRequest pending) =>
        Assert.True(
            _scheduler.Complete(
                1,
                pending,
                new WireReply(
                    new ReplyHeader(pending.Info.RequestId!.Value, 20, 0),
                    new EmptyResult()
                )
            )
        );

    private static void AssertRequest(
        PendingRequest pending,
        bool preparing = false,
        bool prepared = false,
        bool sent = false,
        bool writing = false,
        bool completed = false,
        bool released = false,
        bool tombstone = false
    )
    {
        Assert.Equal(preparing, pending.Preparing);
        Assert.Equal(prepared, pending.Prepared);
        Assert.Equal(sent, pending.Sent);
        Assert.Equal(writing, pending.Writing);
        Assert.Equal(completed, pending.Completed);
        Assert.Equal(released, pending.Released);
        Assert.Equal(tombstone, pending.Tombstone);
    }

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_queued")]
    private static extern ref LinkedList<PendingRequest> Queued(RequestScheduler scheduler);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_tombstones")]
    private static extern ref int Tombstones(RequestScheduler scheduler);

    private sealed class AdmittedSubmit(
        Task<WireReply> submit,
        TaskCompletionSource entered,
        TaskCompletionSource release,
        ThirdWalkGate members
    )
    {
        internal Task<WireReply> Submit { get; } = submit;
        internal TaskCompletionSource Entered { get; } = entered;
        internal TaskCompletionSource Release { get; } = release;
        internal ThirdWalkGate Members { get; } = members;
    }

    private sealed class ThirdWalkGate(
        WireRequest[] items,
        TaskCompletionSource entered,
        TaskCompletionSource release,
        bool failEncode,
        CancellationToken cancellationToken
    ) : IReadOnlyList<WireRequest>
    {
        private int _walks;
        internal int Walks => Volatile.Read(ref _walks);

        public int Count => items.Length;
        public WireRequest this[int index] => items[index];

        public IEnumerator<WireRequest> GetEnumerator()
        {
            var walk = Interlocked.Increment(ref _walks);
            if (walk == 3)
            {
                entered.TrySetResult();
                release.Task.Wait(cancellationToken);
                if (failEncode)
                {
                    throw new InvalidDataException("Encode failed after admission.");
                }
            }
            return ((IEnumerable<WireRequest>)items).GetEnumerator();
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}

public sealed class WatchTransitionTableTests
{
    [Fact(Timeout = 10000)]
    [Trait("Acceptance", "T26")]
    public void HandleTransitionsKeepPendingActiveSuppressedAndAbsentLegal()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        using var fixture = new WatchFixture();
        var data = fixture.Begin("/data", ZooKeeperWatchMode.OneShotData);
        AssertWatch(data, pending: true);
        fixture.Reply(data);
        AssertWatch(data, active: true);
        var removed = fixture.Begin("/removed", ZooKeeperWatchMode.Persistent);
        fixture.Reply(removed);
        var exists = fixture.Begin("/exists", ZooKeeperWatchMode.OneShotExists);
        fixture.Reply(exists, (int)ZooKeeperErrorCode.NoNode);
        AssertWatch(exists, active: true, absent: true);
        var suppressed = fixture.Begin("/children", ZooKeeperWatchMode.OneShotChildren);
        suppressed.Sent.Sent = true;
        lock (fixture.Gate)
        {
            suppressed.Intent.Canceled(suppressed.Sent);
        }
        AssertWatch(suppressed, pending: true, suppressed: true);
        lock (fixture.Gate)
        {
            fixture.Manager.Finish(null);
        }
        Assert.Equal(4, fixture.Manager.Count);
        AssertWatch(data, active: false);
        AssertWatch(removed, active: false);
        AssertWatch(exists, absent: true);
        AssertWatch(suppressed, pending: true, suppressed: true);
        Assert.True(data.Watch.Queue.Reader.Completion.IsCompleted);
        Assert.True(suppressed.Watch.Queue.Reader.Completion.IsCompleted);
    }

    [Theory(Timeout = 10000)]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Acceptance", "T28")]
    public async Task CompletedDeliveryStaysOwnedUntilRelease(bool persistentRemoval)
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        using var fixture = new WatchFixture();
        var registration = fixture.Begin(
            "/node",
            persistentRemoval ? ZooKeeperWatchMode.Persistent : ZooKeeperWatchMode.OneShotData
        );
        fixture.Reply(registration);
        Assert.True(registration.Watch.Active);
        lock (fixture.Gate)
        {
            Assert.True(
                fixture.Manager.Dispatch(
                    new WireNotification(
                        (int)(
                            persistentRemoval
                                ? ZooKeeperWatchEventKind.PersistentWatchRemoved
                                : ZooKeeperWatchEventKind.NodeDataChanged
                        ),
                        WatchProtocol.ServerConnected,
                        "/node",
                        2
                    )
                )
            );
        }
        AssertWatch(registration, active: false);
        Assert.Equal(1, fixture.Manager.Count);
        Assert.Equal(1, registration.Watch.Queue.Reader.Count);
        Assert.True(fixture.Manager.TryRead(registration.Watch, out var delivered));
        Assert.Equal(
            persistentRemoval
                ? ZooKeeperWatchEventKind.PersistentWatchRemoved
                : ZooKeeperWatchEventKind.NodeDataChanged,
            delivered!.Kind
        );
        Assert.True(registration.Watch.Queue.Reader.Completion.IsCompleted);
        Assert.Equal(1, fixture.Manager.Count);
        await registration.Watch.DisposeAsync().ConfigureAwait(true);
        Assert.Equal(0, fixture.Manager.Count);
    }

    [Fact(Timeout = 10000)]
    [Trait("Acceptance", "T29")]
    public void ServerRemovalDuringRestorationExposesRemovingAndRestoring()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        using var held = HoldRemovalDuringRestoration();
        var flags = BucketFlags.Read(held.Fixture.Manager, "/node");
        Assert.Equal(new BucketFlags(false, true, true), flags);
        held.Release.SetResult(held.Reply);
    }

    [Fact(Timeout = 10000)]
    [Trait("Acceptance", "T26")]
    public void RestoredDuringRemovalClearsRestoringAndLeavesDirty()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        using var held = HoldRemovalDuringRestoration();
        held.Fixture.Manager.Restored();
        var flags = BucketFlags.Read(held.Fixture.Manager, "/node");
        Assert.Equal(new BucketFlags(true, true, false), flags);
        held.Fixture.Manager.Restoration(4);
        Assert.Equal(
            new BucketFlags(true, true, false),
            BucketFlags.Read(held.Fixture.Manager, "/node")
        );
        held.Release.SetResult(held.Reply);
    }

    private static HeldRemoval HoldRemovalDuringRestoration()
    {
        WatchFixture? fixture = new WatchFixture();
        try
        {
            var registration = fixture.Begin("/node", ZooKeeperWatchMode.Persistent);
            fixture.Reply(registration);
            var restore = fixture.Manager.Restoration(4);
            Assert.NotEmpty(restore);
            Assert.Equal(
                new BucketFlags(false, false, true),
                BucketFlags.Read(fixture.Manager, "/node")
            );
            var release = new TaskCompletionSource<WireReply>(
                TaskCreationOptions.RunContinuationsAsynchronously
            );
            fixture
                .Session.SubmitWatchRemoval(
                    Arg.Any<WatchRequest>(),
                    Arg.Any<IRequestObserver>(),
                    Arg.Any<CancellationToken>()
                )
                .Returns(release.Task);
            _ = registration.Watch.RemoveAsync(
                WatchRemovalMode.ServerConfirmed,
                TestContext.Current.CancellationToken
            );
            var held = new HeldRemoval(fixture, release);
            fixture = null;
            return held;
        }
        finally
        {
            fixture?.Dispose();
        }
    }

    private static void AssertWatch(
        Registration registration,
        bool pending = false,
        bool active = false,
        bool suppressed = false,
        bool absent = false
    )
    {
        var watch = registration.Watch;
        Assert.Equal(pending, watch.Pending);
        Assert.Equal(active, watch.Active);
        Assert.Equal(suppressed, watch.Suppressed);
        Assert.Equal(absent, watch.Absent);
        Assert.False(watch.Active && watch.Suppressed);
        Assert.False(watch.Pending && watch.Active);
        Assert.False(watch.Pending && watch.Absent);
    }

    private sealed class WatchFixture : IDisposable
    {
        private readonly ClientOptionsSnapshot _options = ClientOptionsSnapshot.Capture(
            SessionFixture.Options(watches: 8, events: 8)
        );
        internal object Gate { get; } = new();
        internal IWatchSession Session { get; } = Substitute.For<IWatchSession>();
        internal WatchManager Manager { get; }
        internal WireReply ReplyValue { get; } = new(new ReplyHeader(1, 1, 0), new EmptyResult());

        internal WatchFixture()
        {
            var telemetry = new TelemetryState();
            Session.Telemetry.Returns(telemetry);
            Session.Session.Returns(
                new ZooKeeperSessionSnapshot(
                    ZooKeeperSessionState.Restoring,
                    1,
                    TimeSpan.FromSeconds(10),
                    TimeSpan.FromSeconds(10),
                    1,
                    0,
                    ZooKeeperCloseStatus.None,
                    null
                )
            );
            Manager = new WatchManager(Gate, _options, new EventBudget(Gate, 8, telemetry), Session);
        }

        internal Registration Begin(string path, ZooKeeperWatchMode mode)
        {
            var info = new ZooKeeperRequestInfo(ZooKeeperOperation.Exists, path: path);
            var sent = SessionTestSupport.CreatePendingRequest(info, _options, Gate);
            return new Registration(Manager.Begin(path, mode, info), sent);
        }

        internal void Reply(Registration registration, int error = 0)
        {
            var reply =
                error == 0
                    ? ReplyValue
                    : new WireReply(new ReplyHeader(1, 1, error), new EmptyResult());
            lock (Gate)
            {
                registration.Intent.Replied(registration.Sent, reply);
            }
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

    private sealed record Registration(WatchManager.WatchIntent Intent, PendingRequest Sent)
    {
        internal ZooKeeperWatch Watch => Intent.Watch;
    }

    private sealed class HeldRemoval(WatchFixture fixture, TaskCompletionSource<WireReply> release)
        : IDisposable
    {
        internal WatchFixture Fixture { get; } = fixture;
        internal TaskCompletionSource<WireReply> Release { get; } = release;
        internal WireReply Reply => Fixture.ReplyValue;

        public void Dispose()
        {
            Release.TrySetResult(Fixture.ReplyValue);
            Fixture.Dispose();
        }
    }

    private readonly record struct BucketFlags(bool Dirty, bool Removing, bool Restoring)
    {
        internal static BucketFlags Read(WatchManager manager, string path)
        {
            var buckets = (IDictionary)
                typeof(WatchManager)
                    .GetField("_buckets", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .GetValue(manager)!;
            foreach (DictionaryEntry entry in buckets)
            {
                var keyPath = (string)entry.Key.GetType().GetProperty("Path")!.GetValue(entry.Key)!;
                if (!string.Equals(keyPath, path, StringComparison.Ordinal))
                {
                    continue;
                }
                var bucket = entry.Value!;
                bool Flag(string name) =>
                    (bool)
                        bucket
                            .GetType()
                            .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!
                            .GetValue(bucket)!;
                return new BucketFlags(Flag("Dirty"), Flag("Removing"), Flag("Restoring"));
            }
            throw new InvalidOperationException("The watch bucket is not owned.");
        }
    }
}

public sealed class SessionTransitionTableTests : IAsyncLifetime
{
    private readonly SessionFixture _fixture = new();
    private readonly List<SessionPeer> _peers = [];

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
    public async Task ClosePublishesClosingBeforeItBecomesClosed()
    {
        var peer = Peer();
        var client = await ConnectAsync(peer).ConfigureAwait(true);
        var states = new StateObserver(client);
        await using var statesLifetime = states.ConfigureAwait(true);
        var close = client.CloseAsync(TestContext.Current.CancellationToken);
        var closing = await states
            .WaitAsync(ZooKeeperSessionState.Closing)
            .ConfigureAwait(true);
        Assert.Equal(ZooKeeperSessionState.Closing, closing.State);
        await SessionTestSupport.Bounded(close).ConfigureAwait(true);
        Assert.Equal(ZooKeeperSessionState.Closed, client.Session.State);
        Assert.Equal(ZooKeeperCloseStatus.Acknowledged, client.Session.CloseStatus);
        Assert.True(CloseAcknowledged(client.Manager));
        Assert.False(ServerExpired(client.Manager));
    }

    [Fact(Timeout = 10000)]
    [Trait("Acceptance", "T10")]
    public async Task ExpiryExcludesCloseAcknowledgement()
    {
        var peer = Peer();
        var client = await ConnectAsync(peer).ConfigureAwait(true);
        var states = new StateObserver(client);
        await using var statesLifetime = states.ConfigureAwait(true);
        var bytes = new byte[64];
        var writer = new JuteWriter(bytes, ProtocolFixtures.Limits);
        writer.WriteReplyHeader(new ReplyHeader(-1, -1, 0));
        writer.WriteInt32(WatchProtocol.SessionEvent);
        writer.WriteInt32((int)ZooKeeperErrorCode.SessionExpired);
        writer.WriteString(null);
        peer.Inject(bytes[..writer.WrittenCount]);
        await states.WaitAsync(ZooKeeperSessionState.Expired).ConfigureAwait(true);
        await SessionTestSupport
            .Bounded(client.CloseAsync(TestContext.Current.CancellationToken))
            .ConfigureAwait(true);
        Assert.Equal(ZooKeeperCloseStatus.SessionExpired, client.Session.CloseStatus);
        Assert.Equal(ZooKeeperSessionState.Expired, client.Session.State);
        Assert.True(ServerExpired(client.Manager));
        Assert.False(CloseAcknowledged(client.Manager));
        Assert.Equal(0, peer.Requests.Count);
    }

    [Theory(Timeout = 10000)]
    [InlineData((int)ZooKeeperErrorCode.SessionMoved)]
    [InlineData((int)ZooKeeperErrorCode.UnknownSession)]
    [Trait("Acceptance", "T13")]
    public async Task ReplyDisconnectsAndStopsAdmissionBeforeItReturns(int code)
    {
        var peer = Peer();
        peer.ReplyApplications = false;
        var client = await ConnectAsync(peer).ConfigureAwait(true);
        var call = client.SyncAsync("/a", TestContext.Current.CancellationToken);
        var packet = await SessionTestSupport.NextAsync(peer).ConfigureAwait(true);
        var generation = client.Session.ConnectionGeneration;
        var pending = client.Manager.Scheduler.Expected(generation, packet.Xid, out _);
        Assert.NotNull(pending);
        var failure = client.Manager.Reply(
            generation,
            pending,
            new WireReply(new ReplyHeader(packet.Xid, 12, code), new EmptyResult())
        );
        Assert.Equal(RuntimeStop.Disconnected, failure!.Value.Kind);
        Assert.Equal(code, failure.Value.ErrorCode);
        Assert.Equal(ZooKeeperSessionState.Disconnected, client.Session.State);
        Assert.Equal(0, client.Manager.Scheduler.PendingCount);
        Assert.Equal(0, client.Manager.Scheduler.OwnedBytes);
        var error = await Assert.ThrowsAsync<ZooKeeperException>(() => call).ConfigureAwait(true);
        Assert.Equal(RequestOutcome.Unknown, error.Outcome);
        var rejected = client.SyncAsync("/b", TestContext.Current.CancellationToken);
        var rejectedError = await Assert
            .ThrowsAsync<ZooKeeperException>(() => rejected)
            .ConfigureAwait(true);
        Assert.Equal(RequestOutcome.NotSent, rejectedError.Outcome);
        Assert.False(peer.Requests.TryRead(out _));
    }

    private SessionPeer Peer()
    {
        var peer = new SessionPeer();
        _peers.Add(peer);
        return peer;
    }

    private async Task<ZooKeeperClient> ConnectAsync(SessionPeer peer)
    {
        var client = await SessionTestSupport
            .Bounded(
                _fixture.ConnectAsync(peer, cancellationToken: TestContext.Current.CancellationToken)
            )
            .ConfigureAwait(true);
        var handshake = await SessionTestSupport.NextAsync(peer).ConfigureAwait(true);
        Assert.True(handshake.Handshake);
        return client;
    }

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_closeAcknowledged")]
    private static extern ref bool CloseAcknowledged(SessionManager manager);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_serverExpired")]
    private static extern ref bool ServerExpired(SessionManager manager);
}
