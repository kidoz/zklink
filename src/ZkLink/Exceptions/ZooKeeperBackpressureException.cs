namespace ZkLink;

/// <summary>Local request capacity was unavailable before any request bytes were sent.</summary>
public sealed class ZooKeeperBackpressureException : ZooKeeperException
{
    /// <summary>Creates a NotSent local capacity failure without a server error.</summary>
    public ZooKeeperBackpressureException(ZooKeeperRequestInfo request)
        : base(request, RequestOutcome.NotSent) { }
}
