namespace ZkLink;

/// <summary>Caller cancellation with request-outcome evidence; cancellation does not undo a send.</summary>
public sealed class ZooKeeperOperationCanceledException
    : OperationCanceledException,
        IZooKeeperRequestFailure
{
    /// <summary>Preserves ordinary cancellation handling and the original cancellation token.</summary>
    public ZooKeeperOperationCanceledException(
        ZooKeeperRequestInfo request,
        RequestOutcome outcome,
        CancellationToken cancellationToken = default
    )
        : base("ZooKeeper request wait was cancelled; inspect request outcome.", cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ZooKeeperException.ValidateOutcome(outcome);
        if (outcome == RequestOutcome.Rejected)
        {
            throw new ArgumentOutOfRangeException(nameof(outcome));
        }
        Request = request;
        Outcome = outcome;
    }

    /// <inheritdoc />
    public ZooKeeperRequestInfo Request { get; }

    /// <inheritdoc />
    public RequestOutcome Outcome { get; }

    /// <inheritdoc />
    public int? ErrorCode => null;

    /// <inheritdoc />
    public ZooKeeperErrorCode? KnownErrorCode => null;

    /// <summary>Gets false; for CloseSession this records an unacknowledged close.</summary>
    public bool CloseAcknowledged { get; }
}
