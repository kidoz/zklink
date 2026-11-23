using System.Net.Sockets;
using System.Security.Cryptography;
using ZkLink.Diagnostics;
using ZkLink.Transport;

namespace ZkLink.Sessions;

internal sealed partial class SessionManager
{
    // Carries retry evidence across attempts on the sole session loop.
    private sealed class ConnectionSeries(long initialStarted)
    {
        internal long InitialStarted { get; } = initialStarted;
        internal int FailedCycles;
        internal ZooKeeperConnectionException LastFailure = new(
            ZooKeeperConnectionFailure.Unreachable
        );
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Design",
        "CA1031",
        Justification = "The session loop owns terminal notification and cleanup. Unexpected failures end the session without exposing exception details."
    )]
    private async Task RunAsync()
    {
        var series = new ConnectionSeries(_time.GetTimestamp());
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var budget = ConnectionBudget(series.InitialStarted);
                if (budget <= TimeSpan.Zero)
                {
                    PublishBudgetExpiry(series);
                    break;
                }
                if (!BeginConnectionAttempt())
                {
                    break;
                }
                var (endpoint, last) = _endpoints.Next();
                if (!await RunAttemptAsync(series, endpoint, budget).ConfigureAwait(false))
                {
                    break;
                }
                if (_stop.IsCancellationRequested || _closing || IsTerminal(Session.State))
                {
                    break;
                }
                DisconnectForRetry();
                if (last)
                {
                    var remaining = ConnectionBudget(series.InitialStarted);
                    if (remaining > TimeSpan.Zero)
                    {
                        await Task.Delay(
                                _random.Backoff(series.FailedCycles++, remaining),
                                _time,
                                _stop.Token
                            )
                            .ConfigureAwait(false);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (Exception)
        {
            lock (_gate)
            {
                if (!IsTerminal(Session.State))
                {
                    _terminalCause = new ZooKeeperSessionException(ZooKeeperSessionState.Faulted);
                    PublishTerminal(
                        ZooKeeperSessionState.Faulted,
                        fault: TelemetryFault.Unexpected
                    );
                }
            }
        }
        finally
        {
            await FinishSessionLoopAsync().ConfigureAwait(false);
        }
    }

    private bool BeginConnectionAttempt()
    {
        lock (_gate)
        {
            if (_closing || IsTerminal(Session.State))
            {
                return false;
            }
            PublishState(ZooKeeperSessionState.Connecting);
        }
        return !_stop.IsCancellationRequested && !IsTerminal(Session.State);
    }

    private async Task<bool> RunAttemptAsync(
        ConnectionSeries series,
        ZooKeeperEndpoint endpoint,
        TimeSpan budget
    )
    {
        var attemptBudget =
            budget < _options.Settings.ConnectAttemptTimeout
                ? budget
                : _options.Settings.ConnectAttemptTimeout;
        var transportDeadline = new TransportDeadline(_time, attemptBudget);
        using var deadline = new CancellationTokenSource(attemptBudget, _time);
        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(
            _stop.Token,
            deadline.Token
        );
        TransportConnection? transport = null;
        var handshakeAccepted = false;
        try
        {
            if (_sessionId != 0)
            {
                ZooKeeperTelemetry.Reconnect();
            }
            transport = await ConnectTransportAsync(
                    endpoint,
                    transportDeadline,
                    deadline.Token,
                    attempt.Token
                )
                .ConfigureAwait(false);
            var restoredZxid = await new SessionEstablisher(
                this,
                transport,
                _options,
                _time,
                _sessionId,
                _lastZxid,
                _password
            )
                .RunAsync(
                    () =>
                    {
                        handshakeAccepted = true;
                        AdjustDeadlineAfterHandshake(deadline, series.InitialStarted);
                    },
                    attempt.Token
                )
                .ConfigureAwait(false);
            var runtime = new ConnectionRuntime(
                this,
                transport,
                Scheduler,
                _options,
                _time,
                _work,
                _generation,
                _negotiated!.Value
            );
            if (
                !CommitConnected(
                    runtime,
                    endpoint,
                    restoredZxid,
                    series.InitialStarted,
                    attempt.Token
                )
            )
            {
                return false;
            }
            series.FailedCycles = 0;
            var outcome = await runtime.RunAsync(_stop.Token).ConfigureAwait(false);
            return outcome.Kind
                is not (
                    RuntimeStop.CloseAcknowledged
                    or RuntimeStop.Expired
                    or RuntimeStop.AuthFailed
                    or RuntimeStop.Protocol
                    or RuntimeStop.Lost
                );
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception error)
            when (error
                    is OperationCanceledException
                        or InvalidDataException
                        or IOException
                        or SocketException
            )
        {
            return HandleAttemptFailure(series, endpoint, handshakeAccepted, error);
        }
        finally
        {
            lock (_gate)
            {
                _active = null;
                Scheduler.StopAdmission();
            }
            if (transport is not null)
            {
                await transport.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private async Task<TransportConnection> ConnectTransportAsync(
        ZooKeeperEndpoint endpoint,
        TransportDeadline deadline,
        CancellationToken deadlineToken,
        CancellationToken attemptToken
    )
    {
        var span = ZooKeeperTelemetry.Begin(_time, TelemetryKind.TransportConnect);
        try
        {
            var connection = await _connector
                .ConnectAsync(endpoint, _options, deadline, attemptToken)
                .ConfigureAwait(false);
            span.Complete();
            return connection;
        }
        catch (Exception error)
        {
            span.Complete(
                error is OperationCanceledException canceled
                && deadlineToken.IsCancellationRequested
                && !_stop.IsCancellationRequested
                    ? ClassifyAttemptCancellation(canceled, endpoint)
                    : error
            );
            throw;
        }
    }

    private static ZooKeeperConnectionException ClassifyAttemptCancellation(
        OperationCanceledException error,
        ZooKeeperEndpoint endpoint
    ) =>
        error.InnerException
            is ZooKeeperConnectionException { Failure: ZooKeeperConnectionFailure.Tls } tls
            ? tls
            : new ZooKeeperConnectionException(ZooKeeperConnectionFailure.Timeout, endpoint);

    private void AdjustDeadlineAfterHandshake(CancellationTokenSource deadline, long initialStarted)
    {
        // Recovery now follows session liveness; initial authentication still has a total deadline.
        if (_initialReady.Task.IsCompletedSuccessfully)
        {
            deadline.CancelAfter(Timeout.InfiniteTimeSpan);
            return;
        }
        var remaining = InitialConnectRemaining(initialStarted);
        if (remaining > TimeSpan.Zero)
        {
            deadline.CancelAfter(remaining);
        }
        else
        {
            deadline.Cancel();
        }
    }

    private bool CommitConnected(
        ConnectionRuntime runtime,
        ZooKeeperEndpoint endpoint,
        long restoredZxid,
        long initialStarted,
        CancellationToken token
    )
    {
        lock (_gate)
        {
            token.ThrowIfCancellationRequested();
            if (
                !_initialReady.Task.IsCompletedSuccessfully
                && InitialConnectRemaining(initialStarted) <= TimeSpan.Zero
            )
            {
                throw new OperationCanceledException(token);
            }
            if (_closing || IsTerminal(Session.State))
            {
                return false;
            }
            // Commit restoration and readiness under one gate so disposal
            // cannot fall between clearing the snapshot and becoming Connected.
            // Partial attempts must not advance the watermark of watches
            // whose reset batch has not yet been acknowledged.
            _lastZxid = restoredZxid;
            _watches.Restored();
            _active = runtime;
            PublishState(ZooKeeperSessionState.Connected);
            if (!_watches.Reconnected())
            {
                FaultOverflow();
                return false;
            }
            Scheduler.Attach(_generation, endpoint);
            _connectionReady.TrySetResult();
            _initialReady.TrySetResult();
        }
        return true;
    }

    private bool HandleAttemptFailure(
        ConnectionSeries series,
        ZooKeeperEndpoint endpoint,
        bool handshakeAccepted,
        Exception error
    )
    {
        var failure = error switch
        {
            OperationCanceledException canceled => ClassifyAttemptCancellation(canceled, endpoint),
            InvalidDataException => new ZooKeeperConnectionException(
                ZooKeeperConnectionFailure.Protocol,
                endpoint
            ),
            ZooKeeperConnectionException connection => connection,
            SocketException socket => new ZooKeeperConnectionException(
                ZooKeeperConnectionFailure.Unreachable,
                endpoint,
                null,
                null,
                socket.SocketErrorCode
            ),
            _ => new ZooKeeperConnectionException(ZooKeeperConnectionFailure.Unreachable, endpoint),
        };
        if (
            series.LastFailure.Failure != ZooKeeperConnectionFailure.Tls
            || failure.Failure
                is not (
                    ZooKeeperConnectionFailure.Unreachable
                    or ZooKeeperConnectionFailure.Timeout
                )
        )
        {
            series.LastFailure = failure;
        }
        if (
            failure.Failure
                is ZooKeeperConnectionFailure.SessionRejected
                    or ZooKeeperConnectionFailure.Authentication
            || (handshakeAccepted && failure.Failure == ZooKeeperConnectionFailure.Protocol)
        )
        {
            PublishConnectionFailure(failure);
            return false;
        }
        return true;
    }

    private void PublishConnectionFailure(ZooKeeperConnectionException error)
    {
        lock (_gate)
        {
            _terminalCode = error.ErrorCode;
            _terminalCause = error;
            var state =
                error.Failure == ZooKeeperConnectionFailure.Authentication
                    ? ZooKeeperSessionState.AuthFailed
                : error.Failure == ZooKeeperConnectionFailure.SessionRejected && _sessionId != 0
                    ? ZooKeeperSessionState.Expired
                : ZooKeeperSessionState.Faulted;
            _serverExpired = state == ZooKeeperSessionState.Expired;
            if (
                _initialReady.Task.IsCompletedSuccessfully
                && state is ZooKeeperSessionState.Expired or ZooKeeperSessionState.AuthFailed
            )
            {
                _terminalCause = new ZooKeeperSessionException(state, _terminalCode);
            }
            PublishTerminal(
                state,
                fault: error.Failure == ZooKeeperConnectionFailure.Protocol
                    ? TelemetryFault.Protocol
                    : TelemetryFault.Connection
            );
        }
    }

    private void PublishBudgetExpiry(ConnectionSeries series)
    {
        lock (_gate)
        {
            if (
                _sessionId == 0
                || (
                    !_initialReady.Task.IsCompletedSuccessfully
                    && InitialConnectRemaining(series.InitialStarted) <= TimeSpan.Zero
                )
            )
            {
                _terminalCause = new ZooKeeperConnectionException(
                    series.LastFailure.Failure == ZooKeeperConnectionFailure.Unreachable
                        ? ZooKeeperConnectionFailure.Timeout
                        : series.LastFailure.Failure,
                    series.LastFailure.Endpoint,
                    series.LastFailure.ErrorCode,
                    series.LastFailure.TlsFailureReason,
                    series.LastFailure.SocketError,
                    series.LastFailure.ProviderFailureReason
                );
                PublishTerminal(
                    ZooKeeperSessionState.Faulted,
                    fault: series.LastFailure.Failure == ZooKeeperConnectionFailure.Protocol
                        ? TelemetryFault.Protocol
                        : TelemetryFault.Connection
                );
            }
            else
            {
                _terminalCause = new ZooKeeperSessionException(ZooKeeperSessionState.Lost);
                PublishTerminal(ZooKeeperSessionState.Lost);
            }
        }
    }

    private void DisconnectForRetry()
    {
        lock (_gate)
        {
            if (_sessionId != 0)
            {
                PublishState(ZooKeeperSessionState.Disconnected);
                if (_connectionReady.Task.IsCompleted)
                {
                    _connectionReady = NewSignal();
                }
            }
            DetachRequests();
        }
    }

    private async Task FinishSessionLoopAsync()
    {
        lock (_gate)
        {
            Scheduler.StopAdmission();
            DetachRequests();
            _watches.Finish(_terminalCause);
            if (!_closing && !IsTerminal(Session.State))
            {
                _closeStatus = ZooKeeperCloseStatus.Unacknowledged;
                PublishState(ZooKeeperSessionState.Closed);
                _states.Finish(Session, null);
            }
            if (!_initialReady.Task.IsCompleted)
            {
                _initialReady.TrySetException(
                    _terminalCause as ZooKeeperConnectionException
                        ?? new ZooKeeperConnectionException(ZooKeeperConnectionFailure.Unreachable)
                );
            }
            _ = _initialReady.Task.Exception;
            if (IsTerminal(Session.State))
            {
                _connectionReady.TrySetException(
                    _terminalCause ?? new ZooKeeperSessionException(Session.State, _terminalCode)
                );
                _ = _connectionReady.Task.Exception;
            }
        }
        await Scheduler.DrainPreparersAsync().ConfigureAwait(false);
        await _cancelCompletion.ConfigureAwait(false);
        CryptographicOperations.ZeroMemory(_password);
        _options.Dispose();
    }
}
