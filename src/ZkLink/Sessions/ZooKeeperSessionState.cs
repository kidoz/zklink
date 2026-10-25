namespace ZkLink;

/// <summary>Local client states, distinct from ZooKeeper wire KeeperState values.</summary>
public enum ZooKeeperSessionState
{
    /// <summary>Connecting or resuming the same session.</summary>
    Connecting = 0,

    /// <summary>Replaying configured authentication.</summary>
    Authenticating = 1,

    /// <summary>Preparing readiness after authentication.</summary>
    Restoring = 2,

    /// <summary>Ready to admit application requests.</summary>
    Connected = 3,

    /// <summary>Transport lost; application admission is suspended.</summary>
    Disconnected = 4,

    /// <summary>The server confirmed session expiry.</summary>
    Expired = 5,

    /// <summary>The client abandoned the session locally.</summary>
    Lost = 6,

    /// <summary>Authentication was rejected.</summary>
    AuthFailed = 7,

    /// <summary>An unrecoverable protocol or local failure occurred.</summary>
    Faulted = 8,

    /// <summary>Closing without admitting new requests.</summary>
    Closing = 9,

    /// <summary>Local resources have been closed; inspect CloseStatus for server acknowledgment.</summary>
    Closed = 10,
}
