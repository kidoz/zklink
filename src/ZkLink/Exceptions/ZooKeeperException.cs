namespace ZkLink;

/// <summary>A failed request with explicit outcome evidence and optional raw server error.</summary>
public class ZooKeeperException : Exception, IZooKeeperRequestFailure
{
    /// <summary>Creates a failure without inferring retry safety from its error code.</summary>
    public ZooKeeperException(
        ZooKeeperRequestInfo request,
        RequestOutcome outcome,
        int? errorCode = null
    )
        : base("ZooKeeper request failed; inspect request outcome and error metadata.")
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateOutcome(outcome);
        if (errorCode == 0)
            throw new ArgumentOutOfRangeException(
                nameof(errorCode),
                "Success is not a failure code."
            );
        if (outcome == RequestOutcome.Rejected && errorCode is null)
            throw new ArgumentException(
                "Server rejection requires a server error code.",
                nameof(errorCode)
            );
        if (outcome == RequestOutcome.NotSent && errorCode is not null)
            throw new ArgumentException(
                "An unsent request cannot have a server result.",
                nameof(errorCode)
            );
        Request = request;
        Outcome = outcome;
        ErrorCode = errorCode;
    }

    /// <inheritdoc />
    public ZooKeeperRequestInfo Request { get; }

    /// <inheritdoc />
    public RequestOutcome Outcome { get; }

    /// <inheritdoc />
    public int? ErrorCode { get; }

    /// <inheritdoc />
    public ZooKeeperErrorCode? KnownErrorCode =>
        ErrorCode is { } code && Enum.IsDefined((ZooKeeperErrorCode)code)
            ? (ZooKeeperErrorCode)code
            : null;

    internal static void ValidateOutcome(RequestOutcome outcome)
    {
        if (!Enum.IsDefined(outcome))
            throw new ArgumentOutOfRangeException(nameof(outcome));
    }
}
