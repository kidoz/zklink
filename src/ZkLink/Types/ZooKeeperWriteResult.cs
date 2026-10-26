namespace ZkLink;

/// <summary>A closed success result in write-transaction input order; returned only when all members succeed.</summary>
public abstract class ZooKeeperWriteResult
{
    internal ZooKeeperWriteResult() { }
}

/// <summary>A created node's actual path and Stat.</summary>
public sealed class ZooKeeperCreatedResult : ZooKeeperWriteResult
{
    internal ZooKeeperCreatedResult(CreateResult value) => Value = value;

    /// <summary>Gets the immutable created-node result.</summary>
    public CreateResult Value { get; }
}

/// <summary>A data update and the resulting Stat.</summary>
public sealed class ZooKeeperDataSetResult : ZooKeeperWriteResult
{
    internal ZooKeeperDataSetResult(ZNodeStat stat) => Stat = stat;

    /// <summary>Gets the resulting node Stat.</summary>
    public ZNodeStat Stat { get; }
}

/// <summary>A successful node deletion within the committed transaction.</summary>
public sealed class ZooKeeperDeletedResult : ZooKeeperWriteResult
{
    internal ZooKeeperDeletedResult() { }
}

/// <summary>A successful data-version check within the committed transaction.</summary>
public sealed class ZooKeeperCheckedResult : ZooKeeperWriteResult
{
    internal ZooKeeperCheckedResult() { }
}
