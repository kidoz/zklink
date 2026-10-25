namespace ZkLink;

/// <summary>An immutable ACL collection and its associated stat.</summary>
public sealed class ZNodeAcl
{
    /// <summary>Copies the ACL collection; entries are immutable.</summary>
    public ZNodeAcl(IReadOnlyList<ZooKeeperAcl> acl, ZNodeStat stat)
    {
        Acl = Snapshot.List(acl);
        Stat = stat;
    }

    /// <summary>Gets the ACL snapshot.</summary>
    public IReadOnlyList<ZooKeeperAcl> Acl { get; }

    /// <summary>Gets the associated stat, including Aversion.</summary>
    public ZNodeStat Stat { get; }
}
