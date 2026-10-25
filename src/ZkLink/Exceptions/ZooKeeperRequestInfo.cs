namespace ZkLink;

/// <summary>Inspectable request metadata excluded from default exception formatting.</summary>
public sealed class ZooKeeperRequestInfo
{
    /// <summary>Creates metadata; requestId is null before a positive xid is assigned.</summary>
    public ZooKeeperRequestInfo(
        ZooKeeperOperation operation,
        int? requestId = null,
        ZooKeeperEndpoint? endpoint = null,
        string? path = null
    )
    {
        if (!Enum.IsDefined(operation))
            throw new ArgumentOutOfRangeException(nameof(operation));
        if (requestId is <= 0)
            throw new ArgumentOutOfRangeException(nameof(requestId));
        Operation = operation;
        RequestId = requestId;
        Endpoint = endpoint;
        Path = path;
    }

    /// <summary>Gets the operation.</summary>
    public ZooKeeperOperation Operation { get; }

    /// <summary>Gets the operation's numeric wire opcode.</summary>
    public int Opcode => (int)Operation;

    /// <summary>Gets the positive wire xid, or null before assignment.</summary>
    public int? RequestId { get; }

    /// <summary>Gets optional configured host/port context.</summary>
    public ZooKeeperEndpoint? Endpoint { get; }

    /// <summary>Gets the optional client-relative path; never included in default exception text.</summary>
    public string? Path { get; }
}
