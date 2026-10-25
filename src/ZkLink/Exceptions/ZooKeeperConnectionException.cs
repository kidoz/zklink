namespace ZkLink;

/// <summary>Initial connection failed and all partially owned resources were released.</summary>
public sealed class ZooKeeperConnectionException : IOException
{
    /// <summary>Creates safe inspectable connection metadata without retaining a platform inner exception.</summary>
    public ZooKeeperConnectionException(
        ZooKeeperConnectionFailure failure,
        ZooKeeperEndpoint? endpoint = null,
        int? errorCode = null
    )
        : base("ZooKeeper connection failed; inspect connection metadata.")
    {
        if (!Enum.IsDefined(failure))
        {
            throw new ArgumentOutOfRangeException(nameof(failure));
        }
        if (errorCode == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(errorCode));
        }
        Failure = failure;
        Endpoint = endpoint;
        ErrorCode = errorCode;
    }

    /// <summary>Gets a safe failure classification.</summary>
    public ZooKeeperConnectionFailure Failure { get; }

    /// <summary>Gets optional endpoint metadata, excluded from default formatting.</summary>
    public ZooKeeperEndpoint? Endpoint { get; }

    /// <summary>Gets an optional original server error.</summary>
    public int? ErrorCode { get; }
}
