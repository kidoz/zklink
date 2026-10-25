namespace ZkLink;

/// <summary>A snapshot of child names and the parent stat; child order is unspecified.</summary>
public sealed class ZNodeChildren
{
    /// <summary>Copies the child-name collection.</summary>
    public ZNodeChildren(IReadOnlyList<string> children, ZNodeStat stat)
    {
        Children = Snapshot.List(children);
        Stat = stat;
    }

    /// <summary>Gets child names, not absolute paths.</summary>
    public IReadOnlyList<string> Children { get; }

    /// <summary>Gets the parent stat.</summary>
    public ZNodeStat Stat { get; }
}
