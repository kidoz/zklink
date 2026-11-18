namespace ZkLink;

/// <summary>The client matched a reply to this request but could not use its body.</summary>
public sealed class ZooKeeperProtocolException : ZooKeeperException
{
    /// <summary>Creates a protocol failure with an unknown request outcome.</summary>
    public ZooKeeperProtocolException(ZooKeeperRequestInfo request)
        : base(request, RequestOutcome.Unknown) { }
}
