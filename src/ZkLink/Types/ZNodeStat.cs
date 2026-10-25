namespace ZkLink;

/// <summary>A snapshot of the eleven raw ZooKeeper Stat fields in schema order.</summary>
/// <param name="Czxid">Creation zxid.</param>
/// <param name="Mzxid">Last data modification zxid.</param>
/// <param name="Ctime">Creation time in server milliseconds.</param>
/// <param name="Mtime">Data modification time in server milliseconds.</param>
/// <param name="Version">Data version, preserving signed overflow.</param>
/// <param name="Cversion">Child version.</param>
/// <param name="Aversion">ACL version.</param>
/// <param name="EphemeralOwner">Raw owner bits; extended node types need separate interpretation.</param>
/// <param name="DataLength">Data length reported by the server.</param>
/// <param name="NumChildren">Child count reported by the server.</param>
/// <param name="Pzxid">Last child modification zxid.</param>
public readonly record struct ZNodeStat(
    long Czxid,
    long Mzxid,
    long Ctime,
    long Mtime,
    int Version,
    int Cversion,
    int Aversion,
    long EphemeralOwner,
    int DataLength,
    int NumChildren,
    long Pzxid
);
