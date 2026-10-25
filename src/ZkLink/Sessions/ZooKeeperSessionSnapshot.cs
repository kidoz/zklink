namespace ZkLink;

/// <summary>An immutable diagnostic/readiness snapshot without session credentials.</summary>
public sealed class ZooKeeperSessionSnapshot
{
    internal ZooKeeperSessionSnapshot(
        ZooKeeperSessionState state,
        long sessionId,
        TimeSpan requestedTimeout,
        TimeSpan? negotiatedTimeout,
        long connectionGeneration,
        long lastReplyZxid,
        ZooKeeperCloseStatus closeStatus,
        int? errorCode
    )
    {
        State = state;
        SessionId = sessionId;
        RequestedTimeout = requestedTimeout;
        NegotiatedTimeout = negotiatedTimeout;
        ConnectionGeneration = connectionGeneration;
        LastReplyZxid = lastReplyZxid;
        CloseStatus = closeStatus;
        ErrorCode = errorCode;
    }

    /// <summary>Gets local state.</summary>
    public ZooKeeperSessionState State { get; }

    /// <summary>Gets the diagnostic session ID; it does not grant ownership or readiness.</summary>
    public long SessionId { get; }

    /// <summary>Gets the requested timeout.</summary>
    public TimeSpan RequestedTimeout { get; }

    /// <summary>Gets the actual negotiated timeout, if established.</summary>
    public TimeSpan? NegotiatedTimeout { get; }

    /// <summary>Gets the connection generation; it never identifies a replacement session.</summary>
    public long ConnectionGeneration { get; }

    /// <summary>Gets the last processed positive ordinary-reply zxid, excluding notifications.</summary>
    public long LastReplyZxid { get; }

    /// <summary>Gets close acknowledgment/expiry evidence.</summary>
    public ZooKeeperCloseStatus CloseStatus { get; }

    /// <summary>Gets an optional raw server error associated with terminal state.</summary>
    public int? ErrorCode { get; }
}
