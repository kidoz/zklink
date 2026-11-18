namespace ZkLink;

/// <summary>A watch event budget or a state subscription limit was exceeded.</summary>
public sealed class ZooKeeperEventOverflowException : ZooKeeperSessionException
{
    internal ZooKeeperEventOverflowException(ZooKeeperSessionState state)
        : base(state, null, "State subscription capacity was exceeded.") { }

    /// <summary>Creates the terminal local overflow fault.</summary>
    public ZooKeeperEventOverflowException()
        : base(ZooKeeperSessionState.Faulted) { }
}
