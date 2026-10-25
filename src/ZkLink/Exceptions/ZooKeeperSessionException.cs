namespace ZkLink;

/// <summary>A terminal session cannot become ready again; creating another client is explicit.</summary>
public class ZooKeeperSessionException : InvalidOperationException
{
    /// <summary>Creates a terminal readiness failure with safe diagnostic metadata.</summary>
    public ZooKeeperSessionException(ZooKeeperSessionState state, int? errorCode = null)
        : base("ZooKeeper session is not recoverable; inspect session state.")
    {
        if (!Enum.IsDefined(state))
        {
            throw new ArgumentOutOfRangeException(nameof(state));
        }
        State = state;
        ErrorCode = errorCode;
    }

    /// <summary>Gets terminal local state.</summary>
    public ZooKeeperSessionState State { get; }

    /// <summary>Gets an optional server error.</summary>
    public int? ErrorCode { get; }
}
