namespace ZkLink;

/// <summary>An owned data snapshot and the corresponding stat.</summary>
public sealed class ZNodeData
{
    /// <summary>Copies data while preserving the distinction between null and empty.</summary>
    public ZNodeData(ReadOnlyMemory<byte>? data, ZNodeStat stat)
    {
        Data = Snapshot.Data(data);
        Stat = stat;
    }

    /// <summary>Gets owned data that never aliases caller input or a reusable pool.</summary>
    public ReadOnlyMemory<byte>? Data { get; }

    /// <summary>Gets the stat observed with the data.</summary>
    public ZNodeStat Stat { get; }
}
