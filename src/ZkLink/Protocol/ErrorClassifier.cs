using ZkLink.Watches;

namespace ZkLink.Protocol;

internal enum ReplyPhase
{
    Application,
    Ping,
    Authentication,
    WatchRestore,
}

internal static class ErrorClassifier
{
    internal static ZooKeeperConnectionFailure? ConnectionImpact(int error, ReplyPhase phase) =>
        (ZooKeeperErrorCode)error switch
        {
            0 => null,
            ZooKeeperErrorCode.SessionExpired => ZooKeeperConnectionFailure.SessionRejected,
            ZooKeeperErrorCode.AuthFailed or ZooKeeperErrorCode.SessionClosedRequiresSaslAuth =>
                ZooKeeperConnectionFailure.Authentication,
            ZooKeeperErrorCode.UnknownSession or ZooKeeperErrorCode.SessionMoved =>
                ZooKeeperConnectionFailure.Unreachable,
            // An ordinary matched reply can fail its own request on a still usable connection.
            ZooKeeperErrorCode.ConnectionLoss
            or ZooKeeperErrorCode.OperationTimeout
            or ZooKeeperErrorCode.RequestTimeout when phase != ReplyPhase.Application =>
                ZooKeeperConnectionFailure.Unreachable,
            _ when phase == ReplyPhase.Application => null,
            _ when phase == ReplyPhase.Authentication => ZooKeeperConnectionFailure.Authentication,
            _ => ZooKeeperConnectionFailure.Protocol,
        };

    // Session notifications carry KeeperState values, a different wire namespace from reply errors.
    internal static ZooKeeperConnectionFailure? NotificationImpact(int state) =>
        state switch
        {
            WatchProtocol.ServerConnected => null,
            WatchProtocol.ServerDisconnected => ZooKeeperConnectionFailure.Unreachable,
            WatchProtocol.ServerExpired => ZooKeeperConnectionFailure.SessionRejected,
            WatchProtocol.ServerAuthFailed => ZooKeeperConnectionFailure.Authentication,
            _ => ZooKeeperConnectionFailure.Protocol,
        };

    internal static int SelectMemberSessionError(int current, int error)
    {
        var impact = ConnectionImpact(error, ReplyPhase.Application);
        if (impact is null)
        {
            return current;
        }
        if (
            current == 0
            || (
                impact != ZooKeeperConnectionFailure.Unreachable
                && ConnectionImpact(current, ReplyPhase.Application)
                    == ZooKeeperConnectionFailure.Unreachable
            )
        )
        {
            return error;
        }
        return current;
    }

    // Transmission succeeded. Only an explicit server rejection establishes a rejected outcome.
    internal static RequestOutcome OutcomeFor(int error) =>
        (ZooKeeperErrorCode)error switch
        {
            ZooKeeperErrorCode.Unimplemented
            or ZooKeeperErrorCode.BadArguments
            or ZooKeeperErrorCode.NoNode
            or ZooKeeperErrorCode.NoAuth
            or ZooKeeperErrorCode.BadVersion
            or ZooKeeperErrorCode.NoChildrenForEphemerals
            or ZooKeeperErrorCode.NodeExists
            or ZooKeeperErrorCode.NotEmpty
            or ZooKeeperErrorCode.SessionExpired
            or ZooKeeperErrorCode.InvalidCallback
            or ZooKeeperErrorCode.InvalidAcl
            or ZooKeeperErrorCode.AuthFailed
            or ZooKeeperErrorCode.NotReadOnly
            or ZooKeeperErrorCode.EphemeralOnLocalSession
            or ZooKeeperErrorCode.NoWatcher
            or ZooKeeperErrorCode.ReconfigurationDisabled
            or ZooKeeperErrorCode.SessionClosedRequiresSaslAuth
            or ZooKeeperErrorCode.QuotaExceeded
            or ZooKeeperErrorCode.ThrottledOperation => RequestOutcome.Rejected,
            _ => RequestOutcome.Unknown,
        };
}
