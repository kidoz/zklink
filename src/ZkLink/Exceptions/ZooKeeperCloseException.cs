namespace ZkLink;

/// <summary>Local cleanup finished without acknowledged server close.</summary>
public sealed class ZooKeeperCloseException : ZooKeeperException
{
    /// <summary>Creates an unacknowledged CloseSession failure with its observed outcome.</summary>
    public ZooKeeperCloseException(
        ZooKeeperRequestInfo request,
        RequestOutcome outcome,
        int? errorCode = null
    )
        : base(request, outcome, errorCode)
    {
        if (request.Operation != ZooKeeperOperation.CloseSession)
            throw new ArgumentException(
                "Close failure requires a CloseSession request.",
                nameof(request)
            );
    }

    /// <summary>Gets false; local cleanup never proves that ephemerals were deleted.</summary>
    public bool CloseAcknowledged { get; }
}
