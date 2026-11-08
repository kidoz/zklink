using System.Collections.Generic;
using System.Runtime.CompilerServices;
using ZkLink.Protocol;
using ZkLink.Requests;
using ZkLink.Sessions;
using ZkLink.Watches;

namespace ZkLink.Protocol.Tests.Sessions;

public sealed class SessionInterleavingTests
{
    private const int Seed = 20261108;
    private const int HeldWriteSeed = 166;
    private const int Depth = 12;
    private const string HeldWriteSteps =
        "CallerSubmit,ReaderNotification,WriterTake,WriteCompletion,CallerSubmit,ReaderReply,ReaderNotification,OperationTimer,CallerClose,HeartbeatSample,HeartbeatSample,HeartbeatSample";

    [Fact(Timeout = 10000)]
    [Trait("Acceptance", "T13")]
    public async Task SeededInterleavingKeepsTheSessionOracles()
    {
        var steps = await RunSeedAsync(Seed, TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        Assert.Equal(Depth, steps.Split(',').Length);
    }

    [Fact(Timeout = 10000)]
    [Trait("Acceptance", "T13")]
    public async Task HeldWriteSeedInterleavesReplyTimerAndClose()
    {
        var steps = await RunSeedAsync(HeldWriteSeed, TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        Assert.Equal(HeldWriteSteps, steps);
    }

    private static async Task<string> RunSeedAsync(int seed, CancellationToken token)
    {
        var fixture = new SessionFixture();
        var peer = new SessionPeer { ReplyApplications = false };
        try
        {
            return await RunAsync(fixture, peer, seed, token).ConfigureAwait(true);
        }
        finally
        {
            peer.ReleaseHeldWrite();
            peer.ReleaseWrite();
            await fixture.DisposeAsync().ConfigureAwait(true);
            await peer.DisposeAsync().ConfigureAwait(true);
        }
    }

    private static async Task<string> RunAsync(
        SessionFixture fixture,
        SessionPeer peer,
        int seed,
        CancellationToken token
    )
    {
        var client = await SessionTestSupport
            .Bounded(fixture.ConnectAsync(peer, cancellationToken: token))
            .ConfigureAwait(true);
        var handshake = await SessionTestSupport.NextAsync(peer).ConfigureAwait(true);
        Assert.True(handshake.Handshake);
        var harness = new Harness(fixture, peer, client, seed);
        await harness.InstallWatchAsync(token).ConfigureAwait(true);
        var mix = seed;
        try
        {
            for (var step = 0; step < Depth; step++)
            {
                var choice = harness.Choose(ref mix);
                harness.Steps.Add(choice.ToString());
                try
                {
                    await harness.ApplyAsync(choice, token).ConfigureAwait(true);
                    harness.CheckOracles();
                }
                catch (Exception error)
                {
                    throw new InvalidOperationException(
                        harness.Sequence("failed on " + choice),
                        error
                    );
                }
            }
            Assert.Equal(Depth, harness.Steps.Count);
            return string.Join(",", harness.Steps);
        }
        finally
        {
            foreach (var call in harness.Calls)
            {
                _ = call.Task.Exception;
                call.Cancel.Dispose();
            }
        }
    }

    private enum Step
    {
        ReaderReply,
        ReaderNotification,
        WriterTake,
        WriteCompletion,
        HeartbeatSample,
        CallerSubmit,
        CallerClose,
        Cancellation,
        OperationTimer,
    }

    private sealed class Harness(
        SessionFixture fixture,
        SessionPeer peer,
        ZooKeeperClient client,
        int seed
    )
    {
        private const int SubmitLimit = 4;
        private readonly List<Call> _calls = [];
        private readonly List<PendingRequest> _tracked = [];
        private readonly Queue<ReceivedPacket> _unreplied = [];
        private readonly HashSet<int> _xids = [];
        private bool _writeHeld;
        private bool _closed;
        private ZooKeeperSessionState? _terminal;
        private long _zxid;
        private ZooKeeperWatch? _watch;
        private int _submits;

        internal List<Call> Calls => _calls;
        internal List<string> Steps { get; } = [];

        internal async Task InstallWatchAsync(CancellationToken token)
        {
            peer.HoldApplicationWrite = true;
            var entered = peer.HoldEntered;
            var adding = client.AddWatchAsync(
                "/node",
                PersistentWatchMode.Persistent,
                token
            );
            await entered.WaitAsync(SessionTestSupport.Guard, token).ConfigureAwait(true);
            _writeHeld = true;
            var packet = await SessionTestSupport.NextAsync(peer).ConfigureAwait(true);
            Remember(packet);
            var pending = Sent(client.Manager.Scheduler).Peek();
            _tracked.Add(pending);
            peer.Reply(packet);
            _unreplied.Clear();
            _watch = await adding.WaitAsync(SessionTestSupport.Guard, token).ConfigureAwait(true);
            CheckOracles();
        }

        internal Step Choose(ref int mix)
        {
            var enabled = new List<Step>();
            if (Open)
            {
                if (_unreplied.Count > 0)
                {
                    enabled.Add(Step.ReaderReply);
                }
                enabled.Add(Step.ReaderNotification);
                if (_writeHeld && Queued(client.Manager.Scheduler).Count > 0)
                {
                    enabled.Add(Step.WriterTake);
                }
                if (_writeHeld && Queued(client.Manager.Scheduler).Count == 0)
                {
                    enabled.Add(Step.WriteCompletion);
                }
                enabled.Add(Step.HeartbeatSample);
                if (_calls.Count(static call => !call.Task.IsCompleted) < SubmitLimit)
                {
                    enabled.Add(Step.CallerSubmit);
                }
                if (_calls.Exists(static call => !call.Task.IsCompleted))
                {
                    enabled.Add(Step.Cancellation);
                    if (
                        client.Manager.InboundSilence + TimeSpan.FromMilliseconds(100)
                        < TimeSpan.FromMilliseconds(350)
                    )
                    {
                        enabled.Add(Step.OperationTimer);
                    }
                }
                enabled.Add(Step.CallerClose);
            }
            else
            {
                enabled.Add(Step.HeartbeatSample);
            }
            mix = unchecked(mix * 1103515245 + 12345);
            return enabled[(int)((uint)mix % (uint)enabled.Count)];
        }

        internal async Task ApplyAsync(Step step, CancellationToken token)
        {
            switch (step)
            {
                case Step.ReaderReply:
                    await ReplyAsync(token).ConfigureAwait(true);
                    break;
                case Step.ReaderNotification:
                    await NotifyAsync(token).ConfigureAwait(true);
                    break;
                case Step.WriterTake:
                    await TakeNextAsync(token).ConfigureAwait(true);
                    break;
                case Step.WriteCompletion:
                    await CompleteWriteAsync(token).ConfigureAwait(true);
                    break;
                case Step.HeartbeatSample:
                    await SampleHeartbeatAsync().ConfigureAwait(true);
                    break;
                case Step.CallerSubmit:
                    await SubmitAsync(token).ConfigureAwait(true);
                    break;
                case Step.CallerClose:
                    await CloseAsync(token).ConfigureAwait(true);
                    break;
                case Step.Cancellation:
                    await CancelAsync(token).ConfigureAwait(true);
                    break;
                case Step.OperationTimer:
                    await FireTimerAsync(token).ConfigureAwait(true);
                    break;
            }
        }

        internal void CheckOracles()
        {
            var sequence = Sequence("oracle");
            var state = client.Session.State;
            if (
                state
                is ZooKeeperSessionState.Expired
                    or ZooKeeperSessionState.Lost
                    or ZooKeeperSessionState.AuthFailed
                    or ZooKeeperSessionState.Faulted
                    or ZooKeeperSessionState.Closed
            )
            {
                _terminal ??= state;
                Assert.Equal(_terminal, state);
            }
            Assert.True(client.Session.LastReplyZxid >= _zxid, sequence);
            _zxid = client.Session.LastReplyZxid;
            Assert.Null(client.Manager.Scheduler.Expected(client.Session.ConnectionGeneration + 1, 1));
            var writing = 0;
            foreach (var pending in Snapshot())
            {
                if (pending.Sent)
                {
                    Assert.True(pending.Prepared, sequence);
                    Assert.False(pending.Preparing, sequence);
                }
                if (pending.Tombstone)
                {
                    Assert.True(pending.Sent, sequence);
                }
                if (pending.Released)
                {
                    Assert.True(pending.Completed, sequence);
                    Assert.False(pending.Preparing, sequence);
                    Assert.False(pending.Writing, sequence);
                }
                Assert.False(pending.Preparing && pending.Prepared, sequence);
                Assert.False(pending.Preparing && pending.Sent, sequence);
                Assert.False(pending.Released && pending.Writing, sequence);
                if (pending.Writing)
                {
                    writing++;
                }
            }
            Assert.True(writing <= 1, sequence);
            var sent = Sent(client.Manager.Scheduler).ToArray();
            for (var index = 1; index < sent.Length; index++)
            {
                Assert.True(
                    sent[index - 1].Info.RequestId < sent[index].Info.RequestId,
                    sequence
                );
            }
            if (_watch is not null)
            {
                Assert.False(_watch.Active && _watch.Suppressed, sequence);
                Assert.False(_watch.Pending && _watch.Active, sequence);
                Assert.False(_watch.Pending && _watch.Absent, sequence);
                if (state == ZooKeeperSessionState.Connected)
                {
                    Assert.True(_watch.Active, sequence);
                    Assert.False(_watch.Suppressed, sequence);
                    Assert.Equal(1, client.Manager.WatchCount);
                }
            }
        }

        internal string Sequence(string reason) =>
            "seed=" + seed + " " + reason + " steps=" + string.Join(",", Steps);

        private bool Open =>
            !_closed
            && client.Session.State is ZooKeeperSessionState.Connected;

        private async Task ReplyAsync(CancellationToken token)
        {
            var packet = _unreplied.Dequeue();
            while (peer.DrainReadWaits()) { }
            peer.Reply(packet);
            var call = _calls.Single(item => item.Xid == packet.Xid);
            if (!call.Task.IsCompleted)
            {
                await call.Task.WaitAsync(SessionTestSupport.Guard, token).ConfigureAwait(true);
            }
            else
            {
                await SessionTestSupport.ReaderWaitingAsync(peer).ConfigureAwait(true);
            }
        }

        private async Task NotifyAsync(CancellationToken token)
        {
            var before = client.Session.LastReplyZxid;
            while (peer.DrainReadWaits()) { }
            var bytes = new byte[256];
            var writer = new JuteWriter(bytes, ProtocolFixtures.Limits);
            writer.WriteReplyHeader(new ReplyHeader(WatchProtocol.NotificationXid, before, 0));
            writer.WriteInt32((int)ZooKeeperWatchEventKind.NodeCreated);
            writer.WriteInt32(WatchProtocol.ServerConnected);
            writer.WriteString("/other");
            peer.Inject(bytes[..writer.WrittenCount]);
            await SessionTestSupport.ReaderWaitingAsync(peer).ConfigureAwait(true);
            Assert.Equal(before, client.Session.LastReplyZxid);
        }

        private async Task TakeNextAsync(CancellationToken token)
        {
            var entered = peer.HoldEntered;
            peer.ReleaseHeldWrite();
            await entered.WaitAsync(SessionTestSupport.Guard, token).ConfigureAwait(true);
            _writeHeld = true;
            Drain();
        }

        private async Task CompleteWriteAsync(CancellationToken token)
        {
            var writing = Snapshot().Single(static pending => pending.Writing);
            peer.ReleaseHeldWrite();
            while (writing.Writing)
            {
                token.ThrowIfCancellationRequested();
                await Task.Yield();
            }
            _writeHeld = false;
            Drain();
        }

        private Task SampleHeartbeatAsync()
        {
            if (!Open)
            {
                return Task.CompletedTask;
            }
            var sampled = client.Manager.InboundTimestamp;
            fixture.Time.Advance(TimeSpan.FromMilliseconds(1));
            client.Manager.Inbound(client.Session.ConnectionGeneration, null);
            Assert.False(
                client.Manager.StopIfInboundUnchanged(
                    client.Session.ConnectionGeneration,
                    sampled,
                    new RuntimeFailure(RuntimeStop.Lost)
                )
            );
            Assert.NotEqual(ZooKeeperSessionState.Lost, client.Session.State);
            return Task.CompletedTask;
        }

        private async Task SubmitAsync(CancellationToken token)
        {
            CancellationTokenSource? cancel = new();
            try
            {
                var path = "/n/" + _submits++;
                Task? held = null;
                if (!_writeHeld)
                {
                    held = peer.HoldEntered;
                }
                var task = client.SyncAsync(path, cancel.Token);
                _calls.Add(new Call(task, cancel, path));
                cancel = null;
                if (held is not null)
                {
                    await held.WaitAsync(SessionTestSupport.Guard, token).ConfigureAwait(true);
                    _writeHeld = true;
                    Drain();
                }
            }
            finally
            {
                cancel?.Dispose();
            }
        }

        private async Task CloseAsync(CancellationToken token)
        {
            var closing = client.CloseAsync(token);
            for (var spin = 0; spin < 8 && !closing.IsCompleted; spin++)
            {
                token.ThrowIfCancellationRequested();
                while (_unreplied.Count > 0 && !closing.IsCompleted)
                {
                    await ReplyAsync(token).ConfigureAwait(true);
                }
                if (!_writeHeld || closing.IsCompleted)
                {
                    await Task.Yield();
                    continue;
                }
                var entered = peer.HoldEntered;
                peer.ReleaseHeldWrite();
                _writeHeld = false;
                var done = await Task
                    .WhenAny(closing, entered)
                    .WaitAsync(SessionTestSupport.Guard, token)
                    .ConfigureAwait(true);
                if (done == entered)
                {
                    _writeHeld = true;
                    Drain();
                }
            }
            await closing.WaitAsync(SessionTestSupport.Guard, token).ConfigureAwait(true);
            _closed = true;
            _writeHeld = false;
        }

        private async Task CancelAsync(CancellationToken token)
        {
            var call = _calls.First(static item => !item.Task.IsCompleted);
            await call.Cancel.CancelAsync().ConfigureAwait(true);
            await Assert
                .ThrowsAnyAsync<OperationCanceledException>(() => call.Task)
                .WaitAsync(SessionTestSupport.Guard, token)
                .ConfigureAwait(true);
        }

        private async Task FireTimerAsync(CancellationToken token)
        {
            var pending = _calls.Where(static call => !call.Task.IsCompleted).ToArray();
            fixture.Time.Advance(TimeSpan.FromMilliseconds(100));
            foreach (var call in pending)
            {
                if (!call.Task.IsCompleted)
                {
                    continue;
                }
                await Assert
                    .ThrowsAnyAsync<ZooKeeperException>(() => call.Task)
                    .WaitAsync(SessionTestSupport.Guard, token)
                    .ConfigureAwait(true);
            }
        }

        private void Drain()
        {
            while (peer.Requests.TryRead(out var packet))
            {
                if (packet.Handshake || packet.Xid <= 0)
                {
                    continue;
                }
                Remember(packet);
                var call = _calls.FirstOrDefault(item => item.Path == PathOf(packet));
                if (call is not null)
                {
                    call.Xid = packet.Xid;
                    _unreplied.Enqueue(packet);
                }
                var pending = Snapshot().FirstOrDefault(item => item.Info.RequestId == packet.Xid);
                if (pending is not null && !_tracked.Contains(pending))
                {
                    _tracked.Add(pending);
                }
            }
        }

        private void Remember(ReceivedPacket packet)
        {
            if (packet.Xid > 0)
            {
                Assert.True(_xids.Add(packet.Xid), Sequence("replayed xid " + packet.Xid));
            }
        }

        private IEnumerable<PendingRequest> Snapshot()
        {
            foreach (var pending in _tracked)
            {
                yield return pending;
            }
            foreach (var pending in Queued(client.Manager.Scheduler))
            {
                if (!_tracked.Contains(pending))
                {
                    yield return pending;
                }
            }
            foreach (var pending in Sent(client.Manager.Scheduler))
            {
                if (!_tracked.Contains(pending))
                {
                    yield return pending;
                }
            }
        }

        private static string PathOf(ReceivedPacket packet)
        {
            var input = new JuteReader(packet.Bytes, ProtocolFixtures.Limits);
            input.ReadRequestHeader();
            return input.ReadString()!;
        }
    }

    private sealed class Call(Task task, CancellationTokenSource cancel, string path)
    {
        internal Task Task { get; } = task;
        internal CancellationTokenSource Cancel { get; } = cancel;
        internal string Path { get; } = path;
        internal int? Xid { get; set; }
    }

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_queued")]
    private static extern ref LinkedList<PendingRequest> Queued(RequestScheduler scheduler);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_sent")]
    private static extern ref Queue<PendingRequest> Sent(RequestScheduler scheduler);
}
