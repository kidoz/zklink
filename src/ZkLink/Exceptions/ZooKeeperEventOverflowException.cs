namespace ZkLink;

/// <summary>The bounded client event budget was exhausted; resnapshot with a new client.</summary>
public sealed class ZooKeeperEventOverflowException : ZooKeeperSessionException
{
    /// <summary>Creates the terminal local overflow fault.</summary>
    public ZooKeeperEventOverflowException()
        : base(ZooKeeperSessionState.Faulted) { }
}
