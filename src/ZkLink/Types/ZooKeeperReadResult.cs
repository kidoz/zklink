namespace ZkLink;

/// <summary>A closed multi-read member result. Results are ordered but do not form an atomic snapshot.</summary>
public abstract class ZooKeeperReadResult
{
    internal ZooKeeperReadResult() { }
}

/// <summary>An owned multi-read data and Stat snapshot.</summary>
public sealed class ZooKeeperDataReadResult : ZooKeeperReadResult
{
    internal ZooKeeperDataReadResult(ZNodeData value) => Value = value;

    /// <summary>Gets owned nullable data and Stat.</summary>
    public ZNodeData Value { get; }
}

/// <summary>Multi-read child names without a parent Stat; order is unspecified.</summary>
public sealed class ZooKeeperChildrenReadResult : ZooKeeperReadResult
{
    internal ZooKeeperChildrenReadResult(IReadOnlyList<string> value) =>
        Value = Snapshot.List(value);

    /// <summary>Gets an immutable child-name snapshot.</summary>
    public IReadOnlyList<string> Value { get; }
}

/// <summary>A member-specific server error; other read results remain independently useful.</summary>
public sealed class ZooKeeperReadErrorResult : ZooKeeperReadResult
{
    internal ZooKeeperReadErrorResult(int errorCode) => ErrorCode = errorCode;

    /// <summary>Gets the original numeric server error.</summary>
    public int ErrorCode { get; }

    /// <summary>Gets the recognized code, otherwise null.</summary>
    public ZooKeeperErrorCode? KnownErrorCode =>
        Enum.IsDefined((ZooKeeperErrorCode)ErrorCode) ? (ZooKeeperErrorCode)ErrorCode : null;
}
