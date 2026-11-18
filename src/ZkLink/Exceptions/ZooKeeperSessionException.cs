namespace ZkLink;

/// <summary>Reports the session state when a session or state-subscription operation fails.</summary>
public class ZooKeeperSessionException : InvalidOperationException
{
    /// <summary>Creates a terminal readiness failure with safe diagnostic metadata.</summary>
    public ZooKeeperSessionException(ZooKeeperSessionState state, int? errorCode = null)
        : this(state, errorCode, "ZooKeeper session is not recoverable; inspect session state.") { }

    internal ZooKeeperSessionException(ZooKeeperSessionState state, int? errorCode, string message)
        : base(message)
    {
        if (!Enum.IsDefined(state))
        {
            throw new ArgumentOutOfRangeException(nameof(state));
        }
        State = state;
        ErrorCode = errorCode;
    }

    /// <summary>Gets the session state at the time of the failure.</summary>
    public ZooKeeperSessionState State { get; }

    /// <summary>Gets an optional server error.</summary>
    public int? ErrorCode { get; }
}
