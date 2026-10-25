namespace ZkLink;

/// <summary>A safe classification of initial connection failure, without platform exception text.</summary>
public enum ZooKeeperConnectionFailure
{
    /// <summary>The connection deadline expired.</summary>
    Timeout = 0,

    /// <summary>DNS or TCP connection failed.</summary>
    Unreachable = 1,

    /// <summary>TLS chain, name or handshake validation failed.</summary>
    Tls = 2,

    /// <summary>The peer returned invalid protocol data.</summary>
    Protocol = 3,

    /// <summary>The server rejected session establishment.</summary>
    SessionRejected = 4,

    /// <summary>Configured authentication was rejected.</summary>
    Authentication = 5,
}
