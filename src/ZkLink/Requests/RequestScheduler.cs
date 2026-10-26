using System.Buffers.Binary;
using System.Security.Cryptography;
using ZkLink.Configuration;
using ZkLink.Protocol;
using ZkLink.Sessions;

namespace ZkLink.Requests;

internal sealed class PendingRequest
{
    internal readonly TaskCompletionSource<WireReply> Completion = new(
        TaskCreationOptions.RunContinuationsAsynchronously
    );

    internal PendingRequest(
        ZooKeeperRequestInfo info,
        int bytes,
        long generation,
        long started,
        CancellationToken token,
        bool control = false
    )
    {
        Info = info;
        ReservedBytes = bytes;
        Generation = generation;
        Started = started;
        Token = token;
        Control = control;
        Task = AwaitAsync(Completion.Task);
    }

    private static async Task<WireReply> AwaitAsync(Task<WireReply> task) =>
        await task.ConfigureAwait(false);

    internal Task<WireReply> Task { get; }
    internal ZooKeeperRequestInfo Info;
    internal readonly int ReservedBytes;
    internal readonly long Generation;
    internal readonly long Started;
    internal readonly CancellationToken Token;
    internal readonly bool Control;
    internal LinkedListNode<PendingRequest>? Node;
    internal byte[]? Packet;
    internal WireRequest? Shape;
    internal bool Preparing = true;
    internal bool Prepared;
    internal bool Sent;
    internal bool Writing;
    internal bool Completed;
    internal bool Released;
    internal CancellationTokenRegistration Cancellation;
    internal ITimer? Timer;
    internal RequestOutcome Outcome => Sent ? RequestOutcome.Unknown : RequestOutcome.NotSent;
}

internal sealed class RequestScheduler(
    object gate,
    ClientOptionsSnapshot options,
    TimeProvider timeProvider,
    AsyncPulse work
)
{
    private readonly LinkedList<PendingRequest> _queued = [];
    private readonly Queue<PendingRequest> _sent = [];
    private readonly HashSet<int> _xids = [];
    private long _generation;
    private ZooKeeperEndpoint? _endpoint;
    private int _bytes;
    private int _inFlight;
    private int _preparing;
    private int _nextXid = 1;
    private bool _accepting;
    private bool _pingQueued;
    private bool _pingSent;
    private PendingRequest? _close;
    private TaskCompletionSource _preparersDrained = CompletedSignal();

    internal int OwnedBytes
    {
        get
        {
            lock (gate)
            {
                return _bytes;
            }
        }
    }
    internal int PendingCount
    {
        get
        {
            lock (gate)
            {
                return _queued.Count + _sent.Count;
            }
        }
    }
    internal bool PingPending
    {
        get
        {
            lock (gate)
            {
                return _pingQueued || _pingSent;
            }
        }
    }

    internal bool IsAttached(long value)
    {
        lock (gate)
        {
            return _generation != 0 && _generation == value;
        }
    }

    internal void SetNextXidForTesting(int value)
    {
        lock (gate)
        {
            _nextXid = value > 0 ? value : throw new ArgumentOutOfRangeException(nameof(value));
        }
    }

    internal void Attach(long value, ZooKeeperEndpoint target)
    {
        lock (gate)
        {
            _generation = value;
            _endpoint = target;
            _accepting = true;
            _pingQueued = false;
            _pingSent = false;
            _close = null;
        }
    }

    internal void StopAdmission()
    {
        lock (gate)
        {
            _accepting = false;
        }
    }

    internal Task DrainPreparersAsync()
    {
        lock (gate)
        {
            return _preparersDrained.Task;
        }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Design",
        "CA1031",
        Justification = "Preparation exceptions complete the caller Task after returning reserved capacity; they are not swallowed."
    )]
    internal Task<WireReply> Submit(
        WireRequest request,
        ZooKeeperRequestInfo info,
        CancellationToken cancellationToken
    )
    {
        var started = timeProvider.GetTimestamp();
        if (cancellationToken.IsCancellationRequested)
        {
            return CancelledAsync(info, cancellationToken);
        }
        if (request.Opcode != info.Opcode)
        {
            throw new ArgumentException("Request metadata must match the opcode.", nameof(info));
        }
        if (
            request is AuthRequest or RestoreRequest or WatchRequest
            || request is ReadRequest { Watch: true }
            || request.Opcode is 11 or -11
        )
        {
            throw new NotSupportedException(
                "This application request is not enabled by the current client API."
            );
        }
        var size = NamespaceTranslator.Measure(request, options.Paths, options.Limits);
        PendingRequest pending;
        lock (gate)
        {
            if (!_accepting)
            {
                return Task.FromException<WireReply>(
                    new ZooKeeperException(info, RequestOutcome.NotSent)
                );
            }
            if (
                _queued.Count >= options.Settings.MaxQueuedRequests
                || size > options.Settings.MaxPendingRequestBytes - _bytes
            )
            {
                return Task.FromException<WireReply>(new ZooKeeperBackpressureException(info));
            }
            pending = new PendingRequest(info, size, _generation, started, cancellationToken);
            pending.Node = _queued.AddLast(pending);
            _bytes += size;
            if (_preparing++ == 0)
            {
                _preparersDrained = new(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }
        byte[]? packet = null;
        WireRequest? shape = null;
        Exception? failure = null;
        try
        {
            var translated = NamespaceTranslator.ToServer(request, options.Paths, options.Limits);
            packet = new byte[size];
            RequestCodec.Encode(packet, 1, translated, options.Limits);
            shape = ReplyShape(translated);
        }
        catch (Exception error)
        {
            failure = error;
        }
        lock (gate)
        {
            pending.Packet = packet;
            pending.Shape = shape;
            pending.Preparing = false;
            if (--_preparing == 0)
            {
                _preparersDrained.TrySetResult();
            }
            if (!pending.Completed)
            {
                if (failure is not null)
                {
                    Finish(pending, failure);
                }
                else
                {
                    try
                    {
                        pending.Prepared = true;
                        pending.Cancellation = cancellationToken.UnsafeRegister(
                            static state =>
                            {
                                var pair = ((RequestScheduler Scheduler, PendingRequest Pending))
                                    state!;
                                pair.Scheduler.Cancel(pair.Pending, false);
                            },
                            (this, pending)
                        );
                        var remaining =
                            options.Settings.OperationTimeout
                            - timeProvider.GetElapsedTime(started);
                        if (remaining <= TimeSpan.Zero)
                        {
                            Cancel(pending, true);
                        }
                        else if (!pending.Completed)
                        {
                            pending.Timer = timeProvider.CreateTimer(
                                static state =>
                                {
                                    var pair = ((
                                        RequestScheduler Scheduler,
                                        PendingRequest Pending
                                    ))
                                        state!;
                                    pair.Scheduler.Cancel(pair.Pending, true);
                                },
                                (this, pending),
                                remaining,
                                Timeout.InfiniteTimeSpan
                            );
                        }
                        if (pending.Completed)
                        {
                            pending.Cancellation.Unregister();
                            pending.Timer?.Dispose();
                        }
                    }
                    catch (Exception error)
                    {
                        Finish(pending, error);
                    }
                }
            }
            Release(pending);
        }
        work.Set();
        return pending.Task;
    }

    private static async Task<WireReply> CancelledAsync(
        ZooKeeperRequestInfo info,
        CancellationToken token
    )
    {
        await Task.CompletedTask.ConfigureAwait(false);
        throw new ZooKeeperOperationCanceledException(info, RequestOutcome.NotSent, token);
    }

    private static WireRequest ReplyShape(WireRequest request) =>
        request switch
        {
            SetDataRequest set => set with { Data = null },
            CreateRequest create => create with
            {
                Data = null,
                Acl = Array.AsReadOnly(create.Acl.ToArray()),
            },
            SetAclRequest acl => acl with { Acl = Array.AsReadOnly(acl.Acl.ToArray()) },
            MultiRequest multi => new MultiRequest(
                multi.ReadOnly,
                Array.AsReadOnly(multi.Members.Select(ReplyShape).ToArray())
            ),
            _ => request,
        };

    private void Cancel(PendingRequest pending, bool timeout)
    {
        lock (gate)
        {
            if (pending.Completed || pending.Completion.Task.IsCompleted)
            {
                return;
            }
            Exception error = timeout
                ? new ZooKeeperTimeoutException(pending.Info, pending.Outcome)
                : new ZooKeeperOperationCanceledException(
                    pending.Info,
                    pending.Outcome,
                    pending.Token
                );
            pending.Completion.TrySetException(error);
            pending.Cancellation.Unregister();
            pending.Timer?.Dispose();
            if (!pending.Sent)
            {
                RemoveQueued(pending);
                pending.Completed = true;
                Release(pending);
            }
        }
        work.Set();
    }

    internal void QueuePing()
    {
        lock (gate)
        {
            if (_accepting && !_pingSent)
            {
                _pingQueued = true;
            }
        }
        work.Set();
    }

    internal bool ConsumePing(long value)
    {
        lock (gate)
        {
            if (value != _generation)
            {
                return false;
            }
            if (!_pingSent)
            {
                throw new InvalidDataException("Unexpected ping acknowledgment.");
            }
            _pingSent = false;
            return true;
        }
    }

    internal PendingRequest EnqueueClose()
    {
        lock (gate)
        {
            if (_generation == 0)
            {
                throw new ZooKeeperCloseException(
                    new ZooKeeperRequestInfo(ZooKeeperOperation.CloseSession),
                    RequestOutcome.NotSent
                );
            }
            _accepting = false;
            _pingQueued = false;
            foreach (var pending in _queued.ToArray())
            {
                Finish(pending, new ZooKeeperException(pending.Info, RequestOutcome.NotSent));
            }
            foreach (var pending in _sent)
            {
                pending.Completion.TrySetException(
                    new ZooKeeperException(pending.Info, RequestOutcome.Unknown)
                );
                pending.Cancellation.Unregister();
                pending.Timer?.Dispose();
            }
            if (_close is not null)
            {
                return _close;
            }
            _close = new PendingRequest(
                new ZooKeeperRequestInfo(ZooKeeperOperation.CloseSession),
                0,
                _generation,
                timeProvider.GetTimestamp(),
                default,
                true
            )
            {
                Preparing = false,
                Prepared = true,
                Shape = new EmptyRequest(-11),
                Packet = new byte[8],
            };
            RequestCodec.Encode(_close.Packet, 1, _close.Shape, options.Limits);
            work.Set();
            return _close;
        }
    }

    internal (PendingRequest? Request, bool Ping) Take(long value)
    {
        lock (gate)
        {
            if (value != _generation || _generation == 0)
            {
                return (null, false);
            }
            var pending = _close is { Sent: false, Completed: false } ? _close : null;
            if (pending is null && _pingQueued)
            {
                _pingQueued = false;
                _pingSent = true;
                return (null, true);
            }
            if (pending is null)
            {
                if (
                    !_accepting
                    || _inFlight >= options.Settings.MaxInFlightRequests
                    || _queued.First is not { Value.Prepared: true } head
                )
                {
                    return (null, false);
                }
                pending = head.Value;
                RemoveQueued(pending);
                _inFlight++;
            }
            var xid = AllocateXid();
            pending.Info = new ZooKeeperRequestInfo(
                pending.Info.Operation,
                xid,
                _endpoint,
                pending.Info.Path
            );
            pending.Sent = true;
            pending.Writing = true;
            _sent.Enqueue(pending);
            BinaryPrimitives.WriteInt32BigEndian(pending.Packet!, xid);
            return (pending, false);
        }
    }

    internal void WriteFinished(PendingRequest pending)
    {
        lock (gate)
        {
            pending.Writing = false;
            Release(pending);
        }
    }

    internal PendingRequest? Expected(long value, int xid)
    {
        lock (gate)
        {
            if (value != _generation)
            {
                return null;
            }
            if (!_sent.TryPeek(out var pending) || pending.Info.RequestId != xid)
            {
                throw new InvalidDataException("Reply does not match the pending FIFO.");
            }
            return pending;
        }
    }

    internal PendingRequest? Expected(long value, int xid, out WireRequest? shape)
    {
        lock (gate)
        {
            var pending = Expected(value, xid);
            shape = pending?.Shape;
            return pending;
        }
    }

    internal bool Complete(long value, PendingRequest pending, WireReply reply)
    {
        lock (gate)
        {
            if (value != _generation)
            {
                return false;
            }
            if (!_sent.TryPeek(out var head) || !ReferenceEquals(head, pending))
            {
                throw new InvalidDataException("Pending FIFO changed unexpectedly.");
            }
            _sent.Dequeue();
            _xids.Remove(pending.Info.RequestId!.Value);
            if (!pending.Control)
            {
                _inFlight--;
            }
            pending.Completed = true;
            pending.Cancellation.Unregister();
            pending.Timer?.Dispose();
            if (reply.Header.Error != 0)
            {
                var outcome = reply.Header.Error
                    is -6
                        or -8
                        or -101
                        or -102
                        or -103
                        or -108
                        or -110
                        or -111
                        or -112
                        or -114
                        or -115
                        or -119
                        or -120
                        or -121
                        or -123
                        or -124
                        or -125
                        or -127
                    ? RequestOutcome.Rejected
                    : RequestOutcome.Unknown;
                pending.Completion.TrySetException(
                    new ZooKeeperException(pending.Info, outcome, reply.Header.Error)
                );
            }
            else if (
                pending.Shape is MultiRequest { ReadOnly: false }
                && reply.Result is MultiResult multi
                && !pending.Completion.Task.IsCompleted
            )
            {
                ZooKeeperMultiException? failure = MultiFailure.Create(pending.Info, multi);
                if (failure is not null)
                {
                    pending.Completion.TrySetException(failure);
                }
                else
                {
                    pending.Completion.TrySetResult(reply);
                }
            }
            else
            {
                pending.Completion.TrySetResult(reply);
            }
            Release(pending);
        }
        work.Set();
        return true;
    }

    internal TimeSpan? OldestRemaining(TimeSpan sessionTimeout)
    {
        lock (gate)
        {
            return _sent.TryPeek(out var pending) && !pending.Control
                ? options.Settings.OperationTimeout
                    + sessionTimeout
                    - timeProvider.GetElapsedTime(pending.Started)
                : null;
        }
    }

    internal void Detach()
    {
        lock (gate)
        {
            _accepting = false;
            _generation = 0;
            _pingQueued = false;
            _pingSent = false;
            foreach (var pending in _queued.ToArray())
            {
                Finish(pending, new ZooKeeperException(pending.Info, RequestOutcome.NotSent));
            }
            foreach (var pending in _sent)
            {
                pending.Completed = true;
                pending.Completion.TrySetException(
                    new ZooKeeperException(pending.Info, RequestOutcome.Unknown)
                );
                pending.Cancellation.Unregister();
                pending.Timer?.Dispose();
                Release(pending);
            }
            if (_close is { Completed: false, Sent: false })
            {
                Finish(_close, new ZooKeeperException(_close.Info, RequestOutcome.NotSent));
            }
            _sent.Clear();
            _xids.Clear();
            _inFlight = 0;
        }
        work.Set();
    }

    private void Finish(PendingRequest pending, Exception error)
    {
        RemoveQueued(pending);
        pending.Completed = true;
        pending.Completion.TrySetException(error);
        pending.Cancellation.Unregister();
        pending.Timer?.Dispose();
        Release(pending);
    }

    private void RemoveQueued(PendingRequest pending)
    {
        if (pending.Node is { List: not null })
        {
            _queued.Remove(pending.Node);
        }
        pending.Node = null;
    }

    private void Release(PendingRequest pending)
    {
        if (!pending.Completed || pending.Preparing || pending.Writing || pending.Released)
        {
            return;
        }
        pending.Released = true;
        if (pending.Packet is not null)
        {
            CryptographicOperations.ZeroMemory(pending.Packet);
        }
        pending.Packet = null;
        pending.Shape = null;
        _bytes -= pending.ReservedBytes;
    }

    private int AllocateXid()
    {
        for (var attempt = 0; attempt <= _sent.Count; attempt++)
        {
            var value = _nextXid;
            _nextXid = _nextXid == int.MaxValue ? 1 : _nextXid + 1;
            if (_xids.Add(value))
            {
                return value;
            }
        }
        throw new InvalidOperationException("Positive xid capacity invariant violated.");
    }

    private static TaskCompletionSource CompletedSignal()
    {
        var result = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        result.SetResult();
        return result;
    }
}
