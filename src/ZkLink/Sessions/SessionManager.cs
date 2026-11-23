using System.Security.Cryptography;
using ZkLink.Configuration;
using ZkLink.Diagnostics;
using ZkLink.Protocol;
using ZkLink.Requests;
using ZkLink.Transport;
using ZkLink.Watches;

namespace ZkLink.Sessions;

internal sealed partial class SessionManager : IAsyncDisposable, IWatchSession
{
    internal TelemetryState Telemetry { get; } = new();
    private readonly object _gate = new();
    private readonly ClientOptionsSnapshot _options;
    private readonly TimeProvider _time;
    private readonly ITransportConnector _connector;
    private readonly SessionRandom _random;
    private readonly EndpointProvider _endpoints;
    private readonly StateHub _states;
    private readonly WatchManager _watches;
    private readonly AsyncPulse _work = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly TaskCompletionSource _initialReady = NewSignal();
    private TaskCompletionSource _connectionReady = NewSignal();
    private Task _run = Task.CompletedTask;
    private Task _cancelCompletion = Task.CompletedTask;
    private Task? _closeTask;
    private Task? _disposeTask;
    private ConnectionRuntime? _active;
    private byte[] _password = new byte[16];
    private long _sessionId;
    private long _generation;
    private long _lastZxid;
    private long _lastInbound;
    private TimeSpan? _negotiated;
    private ZooKeeperCloseStatus _closeStatus;
    private int? _terminalCode;
    private Exception? _terminalCause;
    private bool _closing;
    private bool _closeAcknowledged;
    private bool _serverExpired;
    private bool _started;

    internal SessionManager(
        ClientOptionsSnapshot options,
        TimeProvider time,
        ITransportConnector connector,
        SessionRandom random
    )
    {
        ZooKeeperTelemetry.Initialize();
        this._options = options;
        this._time = time;
        this._connector = connector;
        this._random = random;
        _endpoints = new EndpointProvider(_options.Endpoints, _random);
        var events = new EventBudget(_gate, _options.Settings.MaxBufferedEvents, Telemetry);
        _watches = new WatchManager(_gate, _options, events, this);
        _states = new StateHub(
            _gate,
            events.Limit,
            BuildSnapshot(ZooKeeperSessionState.Connecting),
            Telemetry
        );
        Scheduler = new RequestScheduler(_gate, _options, _time, _work, Telemetry);
    }

    // Each multi header or ACL entry consumes at least nine wire bytes.
    internal int MaxInputElements =>
        Math.Min(_options.Limits.MaxCollectionElements, _options.Limits.MaxFrameBytes / 9);

    internal Task Completion => _run;
    internal RequestScheduler Scheduler { get; }
    internal ZooKeeperSessionSnapshot Session => _states.Current;
    internal TimeSpan InboundSilence => _time.GetElapsedTime(Interlocked.Read(ref _lastInbound));
    internal long InboundTimestamp => Interlocked.Read(ref _lastInbound);

    internal bool StopIfInboundUnchanged(long value, long sampled, RuntimeFailure failure)
    {
        lock (_gate)
        {
            if (Interlocked.Read(ref _lastInbound) != sampled)
            {
                return false;
            }
            TransportStopped(value, failure);
            return true;
        }
    }

    internal Task Start()
    {
        lock (_gate)
        {
            if (_started)
            {
                throw new InvalidOperationException("Session already started.");
            }
            _started = true;
        }
        _run = RunAsync();
        return _initialReady.Task;
    }

    internal async Task AbortInitialAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        await _run.ConfigureAwait(false);
    }

    internal Task WaitForConnectionAsync(CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled(cancellationToken);
        }
        lock (_gate)
        {
            if (Session.State == ZooKeeperSessionState.Connected)
            {
                return Task.CompletedTask;
            }
            if (_closing || IsTerminal(Session.State))
            {
                return Task.FromException(
                    _terminalCause ?? new ZooKeeperSessionException(Session.State, _terminalCode)
                );
            }
            return _connectionReady.Task.WaitAsync(cancellationToken);
        }
    }

    internal ZooKeeperStateSubscription Subscribe() => _states.Subscribe();

    internal void UpdateEndpoints(
        IReadOnlyList<ZooKeeperEndpoint> values,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(values);
        if (values.Count is < 1 or > 1024)
        {
            throw new ArgumentOutOfRangeException(nameof(values));
        }
        var copy = Snapshot.List(values);
        _options.ValidateEndpoints(copy);
        lock (_gate)
        {
            if (_closing || IsTerminal(Session.State))
            {
                throw new ZooKeeperSessionException(Session.State, _terminalCode);
            }
            _endpoints.Update(copy);
        }
    }

    internal Task<WireReply> Submit(
        WireRequest request,
        ZooKeeperRequestInfo info,
        CancellationToken token
    ) => Scheduler.Submit(request, info, token);

    private TimeSpan InitialConnectRemaining(long started) =>
        _options.Settings.InitialConnectTimeout - _time.GetElapsedTime(started);

    private TimeSpan ConnectionBudget(long initialStarted)
    {
        var remaining = _initialReady.Task.IsCompletedSuccessfully
            ? TimeSpan.MaxValue
            : InitialConnectRemaining(initialStarted);
        if (_sessionId != 0)
        {
            var recovery = _negotiated!.Value - InboundSilence;
            if (recovery < remaining)
            {
                remaining = recovery;
            }
        }
        return remaining;
    }

    internal void Inbound(long value, ReplyHeader? reply)
    {
        lock (_gate)
        {
            if (_active?.Generation != value || !Scheduler.IsAttached(value))
            {
                return;
            }
            _lastInbound = _time.GetTimestamp();
            if (reply is { Zxid: > 0 } header && header.Zxid != _lastZxid)
            {
                _lastZxid = header.Zxid;
                _states.Publish(BuildSnapshot(Session.State), notify: false);
            }
        }
    }

    internal PendingRequest? Expected(long value, int xid, out WireRequest? shape)
    {
        lock (_gate)
        {
            try
            {
                var pending = Scheduler.Expected(value, xid, out shape);
                if (pending is not null && shape is null)
                {
                    throw new InvalidDataException("Missing expected reply shape.");
                }
                return pending;
            }
            catch (InvalidDataException)
            {
                TransportStopped(value, new(RuntimeStop.Protocol));
                throw;
            }
        }
    }

    private static RuntimeFailure? ToRuntimeFailure(
        ZooKeeperConnectionFailure? impact,
        int? error = null
    ) =>
        impact switch
        {
            null => null,
            ZooKeeperConnectionFailure.SessionRejected => new(RuntimeStop.Expired, error),
            ZooKeeperConnectionFailure.Authentication => new(RuntimeStop.AuthFailed, error),
            ZooKeeperConnectionFailure.Unreachable => new(RuntimeStop.Disconnected, error),
            _ => new(RuntimeStop.Protocol, error),
        };

    internal RuntimeFailure? PingReply(long value, ReplyHeader acknowledgment)
    {
        lock (_gate)
        {
            try
            {
                if (!Scheduler.ConsumePing(value))
                {
                    return new(RuntimeStop.Stopped);
                }
                Inbound(value, null);
                var failure = ToRuntimeFailure(
                    ErrorClassifier.ConnectionImpact(acknowledgment.Error, ReplyPhase.Ping),
                    acknowledgment.Error
                );
                if (failure.HasValue)
                {
                    TransportStopped(value, failure.Value);
                }
                return failure;
            }
            catch (InvalidDataException)
            {
                TransportStopped(value, new(RuntimeStop.Protocol));
                throw;
            }
        }
    }

    internal RuntimeFailure? Reply(long value, PendingRequest pending, WireReply reply)
    {
        lock (_gate)
        {
            try
            {
                if (
                    _active?.Generation != value
                    || Scheduler.Expected(value, reply.Header.Xid) is null
                )
                {
                    return new(RuntimeStop.Stopped);
                }
                Inbound(value, reply.Header);
                int errorCode = reply.Result switch
                {
                    MultiResult multi => multi.MemberSessionError,
                    InvalidReplyBodyResult invalid => invalid.MemberSessionError,
                    _ => reply.Header.Error,
                };
                var result = ToRuntimeFailure(
                    ErrorClassifier.ConnectionImpact(errorCode, ReplyPhase.Application),
                    errorCode
                );
                if (result is { Kind: RuntimeStop.Expired or RuntimeStop.AuthFailed })
                {
                    _terminalCode = errorCode;
                    _serverExpired = result.Value.Kind == RuntimeStop.Expired;
                    _terminalCause = new ZooKeeperSessionException(
                        _serverExpired
                            ? ZooKeeperSessionState.Expired
                            : ZooKeeperSessionState.AuthFailed,
                        _terminalCode
                    );
                    PublishTerminal(
                        _serverExpired
                            ? ZooKeeperSessionState.Expired
                            : ZooKeeperSessionState.AuthFailed,
                        detach: false
                    );
                }
                if (result is { Kind: RuntimeStop.Disconnected })
                {
                    Scheduler.StopAdmission();
                    if (!_closing)
                    {
                        PublishState(ZooKeeperSessionState.Disconnected);
                        if (_connectionReady.Task.IsCompleted)
                        {
                            _connectionReady = NewSignal();
                        }
                    }
                }
                if (pending.Info.Operation == ZooKeeperOperation.CloseSession && errorCode == 0)
                {
                    _closeAcknowledged = true;
                    _closeStatus = ZooKeeperCloseStatus.Acknowledged;
                    _states.Publish(BuildSnapshot(Session.State), false);
                    result = new(RuntimeStop.CloseAcknowledged);
                }
                Scheduler.Complete(value, pending, reply);
                if (
                    result is
                    {
                        Kind: RuntimeStop.Expired
                            or RuntimeStop.AuthFailed
                            or RuntimeStop.Disconnected
                    }
                )
                {
                    DetachRequests();
                }
                return result;
            }
            catch (InvalidDataException)
            {
                TransportStopped(value, new(RuntimeStop.Protocol));
                throw;
            }
        }
    }

    internal RuntimeFailure? Notification(long value, WireNotification notification)
    {
        lock (_gate)
        {
            if (_active?.Generation != value || !Scheduler.IsAttached(value))
            {
                return new(RuntimeStop.Stopped);
            }
            RuntimeFailure? result;
            if (!notification.IsRecognized)
            {
                result = new(RuntimeStop.Protocol);
            }
            else if (notification.Type != WatchProtocol.SessionEvent)
            {
                if (notification.State != WatchProtocol.ServerConnected)
                {
                    result = new(RuntimeStop.Protocol);
                }
                else
                {
                    Inbound(value, null);
                    if (!_watches.Dispatch(notification))
                    {
                        FaultOverflow();
                        result = new(RuntimeStop.Protocol);
                    }
                    else
                    {
                        return null;
                    }
                }
            }
            else
            {
                Inbound(value, null);
                result = ToRuntimeFailure(ErrorClassifier.NotificationImpact(notification.State));
            }
            if (result is { } failure)
            {
                TransportStopped(value, failure);
            }
            return result;
        }
    }

    internal void TransportStopped(long value, RuntimeFailure failure)
    {
        lock (_gate)
        {
            if (
                _active?.Generation != value
                || !Scheduler.IsAttached(value)
                || IsTerminal(Session.State)
            )
            {
                return;
            }
            Scheduler.StopAdmission();
            if (
                failure.Kind
                is RuntimeStop.Expired
                    or RuntimeStop.AuthFailed
                    or RuntimeStop.Protocol
                    or RuntimeStop.Lost
            )
            {
                var state = failure.Kind switch
                {
                    RuntimeStop.Expired => ZooKeeperSessionState.Expired,
                    RuntimeStop.AuthFailed => ZooKeeperSessionState.AuthFailed,
                    RuntimeStop.Lost => ZooKeeperSessionState.Lost,
                    _ => ZooKeeperSessionState.Faulted,
                };
                _serverExpired = state == ZooKeeperSessionState.Expired;
                _terminalCode = failure.ErrorCode;
                _terminalCause = new ZooKeeperSessionException(state, _terminalCode);
                PublishTerminal(state);
            }
            else if (!_closing && failure.Kind == RuntimeStop.Disconnected)
            {
                PublishState(ZooKeeperSessionState.Disconnected);
                if (_connectionReady.Task.IsCompleted)
                {
                    _connectionReady = NewSignal();
                }
                DetachRequests();
            }
        }
    }

    internal Task CloseAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_closeTask is not null)
            {
                return _closeTask;
            }
            _closing = true;
            _watches.Finish(null);
            Scheduler.StopAdmission();
            if (!IsTerminal(Session.State))
            {
                PublishState(ZooKeeperSessionState.Closing);
            }
            var closeSpan = ZooKeeperTelemetry.Begin(_time, TelemetryKind.Close);
            _closeTask = ZooKeeperTelemetry.ObserveAsync(
                CloseCoreAsync(_time.GetTimestamp(), cancellationToken),
                closeSpan
            );
            return _closeTask;
        }
    }

    private async Task CloseCoreAsync(long startedAt, CancellationToken cancellationToken)
    {
        await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        PendingRequest? pending = null;
        Exception? failure = null;
        try
        {
            bool expired;
            bool canSend;
            lock (_gate)
            {
                expired = _serverExpired;
                canSend =
                    _active is not null
                    && Scheduler.IsAttached(_active.Generation)
                    && !IsTerminal(Session.State);
            }
            if (!expired)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!canSend)
                {
                    throw new ZooKeeperCloseException(
                        new ZooKeeperRequestInfo(ZooKeeperOperation.CloseSession),
                        RequestOutcome.NotSent
                    );
                }
                var remaining = _options.Settings.CloseTimeout - _time.GetElapsedTime(startedAt);
                if (remaining <= TimeSpan.Zero)
                {
                    throw new TimeoutException();
                }
                pending = Scheduler.EnqueueClose();
                await pending
                    .Task.WaitAsync(remaining, _time, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            var frozen = Scheduler.FreezeClose(pending);
            failure = new ZooKeeperOperationCanceledException(
                frozen.Info,
                frozen.Outcome,
                cancellationToken
            );
        }
        catch (Exception error) when (error is TimeoutException or ZooKeeperException)
        {
            if (!_serverExpired)
            {
                var frozen = Scheduler.FreezeClose(pending);
                failure = new ZooKeeperCloseException(
                    frozen.Info,
                    frozen.Outcome,
                    error is ZooKeeperException requestFailure ? requestFailure.ErrorCode : null
                );
            }
        }
        finally
        {
            await _stop.CancelAsync().ConfigureAwait(false);
            await _run.ConfigureAwait(false);
            if (pending?.Task.IsFaulted == true)
            {
                _ = pending.Task.Exception;
            }
            lock (_gate)
            {
                _closeStatus =
                    _serverExpired ? ZooKeeperCloseStatus.SessionExpired
                    : _closeAcknowledged ? ZooKeeperCloseStatus.Acknowledged
                    : ZooKeeperCloseStatus.Unacknowledged;
                if (!IsTerminal(Session.State))
                {
                    PublishState(ZooKeeperSessionState.Closed);
                }
                else
                {
                    _states.Publish(BuildSnapshot(Session.State), false);
                }
                _states.Finish(Session, _terminalCause);
                _connectionReady.TrySetException(
                    _terminalCause ?? new ZooKeeperSessionException(Session.State, _terminalCode)
                );
                _ = _connectionReady.Task.Exception;
            }
        }
        if (failure is not null && !_closeAcknowledged && !_serverExpired)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            return new ValueTask(_disposeTask ??= DisposeCoreAsync());
        }
    }

    private async Task DisposeCoreAsync()
    {
        try
        {
            await CloseAsync(default).ConfigureAwait(false);
        }
        catch (ZooKeeperCloseException) { }
        catch (OperationCanceledException) { }
        finally
        {
            _options.Dispose();
            _stop.Dispose();
            lock (_gate)
                Telemetry.Dispose(_time);
        }
    }

    private void PublishState(ZooKeeperSessionState state)
    {
        Telemetry.State(state, _time);
        _states.Publish(BuildSnapshot(state), Session.State != state);
    }

    private void FaultOverflow()
    {
        if (_terminalCause is not ZooKeeperEventOverflowException)
        {
            ZooKeeperTelemetry.Overflow();
            ZooKeeperTelemetry.Fault(TelemetryFault.Overflow);
        }
        Telemetry.State(ZooKeeperSessionState.Faulted, _time);
        _terminalCause = new ZooKeeperEventOverflowException();
        _watches.Finish(_terminalCause);
        Scheduler.StopAdmission();
        DetachRequests();
        _states.Finish(BuildSnapshot(ZooKeeperSessionState.Faulted), _terminalCause);
        _cancelCompletion = _stop.CancelAsync();
        _connectionReady.TrySetException(_terminalCause);
        _ = _connectionReady.Task.Exception;
    }

    // The fault category is counted only for Faulted, a local failure with no server evidence.
    private void PublishTerminal(
        ZooKeeperSessionState state,
        bool detach = true,
        TelemetryFault fault = TelemetryFault.Protocol
    )
    {
        if (state == ZooKeeperSessionState.Faulted)
        {
            ZooKeeperTelemetry.Fault(fault);
        }
        _watches.Finish(_terminalCause ?? new ZooKeeperSessionException(state, _terminalCode));
        Scheduler.StopAdmission();
        PublishState(state);
        if (detach)
        {
            DetachRequests();
        }
        _states.Finish(Session, _terminalCause);
        _connectionReady.TrySetException(
            _terminalCause ?? new ZooKeeperSessionException(state, _terminalCode)
        );
        _ = _connectionReady.Task.Exception;
    }

    private ZooKeeperSessionSnapshot BuildSnapshot(ZooKeeperSessionState state) =>
        new(
            state,
            _sessionId,
            _options.Settings.SessionTimeout,
            _negotiated,
            _generation,
            _lastZxid,
            _closeStatus,
            _terminalCode
        );

    private static bool IsTerminal(ZooKeeperSessionState state) =>
        state
            is ZooKeeperSessionState.Expired
                or ZooKeeperSessionState.Lost
                or ZooKeeperSessionState.AuthFailed
                or ZooKeeperSessionState.Faulted
                or ZooKeeperSessionState.Closed;

    private static TaskCompletionSource NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}
