using System.Security.Cryptography;
using ZkLink.Diagnostics;
using ZkLink.Protocol;
using ZkLink.Watches;

namespace ZkLink.Sessions;

internal sealed partial class SessionManager
{
    internal TimeSpan AcceptHandshake(ConnectResponse handshake)
    {
        lock (_gate)
        {
            if (_sessionId != 0 && _sessionId != handshake.SessionId)
            {
                throw new InvalidDataException("Peer replaced an owned session.");
            }
            _sessionId = handshake.SessionId;
            _negotiated = TimeSpan.FromMilliseconds(handshake.TimeoutMilliseconds);
            ZooKeeperTelemetry.RecordNegotiated(_negotiated.Value);
            CryptographicOperations.ZeroMemory(_password);
            handshake.Password.Span.CopyTo(_password);
            _generation = checked(_generation + 1);
            _lastInbound = _time.GetTimestamp();
            PublishState(ZooKeeperSessionState.Authenticating);
            return _negotiated.Value;
        }
    }

    internal void EstablishmentInbound() => _lastInbound = _time.GetTimestamp();

    internal void BeginWatchRestoration()
    {
        lock (_gate)
        {
            PublishState(ZooKeeperSessionState.Restoring);
        }
    }

    internal IReadOnlyList<RestoreRequest> WatchRestoration()
    {
        lock (_gate)
        {
            return _watches.Restoration(_lastZxid);
        }
    }

    internal void DispatchRestoreNotification(WireNotification notification)
    {
        lock (_gate)
        {
            if (!_watches.Dispatch(notification))
            {
                FaultOverflow();
                throw new OperationCanceledException(_stop.Token);
            }
        }
    }
}
