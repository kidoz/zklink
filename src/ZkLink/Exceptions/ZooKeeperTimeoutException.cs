namespace ZkLink;

/// <summary>The local operation deadline expired; transmission may already have begun.</summary>
public sealed class ZooKeeperTimeoutException : ZooKeeperException
{
    /// <summary>Creates a local deadline failure with the observed transmission outcome.</summary>
    public ZooKeeperTimeoutException(ZooKeeperRequestInfo request, RequestOutcome outcome)
        : base(request, outcome)
    {
        if (outcome == RequestOutcome.Rejected)
        {
            throw new ArgumentOutOfRangeException(nameof(outcome));
        }
    }
}
