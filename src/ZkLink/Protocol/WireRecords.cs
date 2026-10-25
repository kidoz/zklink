namespace ZkLink.Protocol;

// Internal wire records remain separate from public immutable result models.
internal readonly record struct RequestHeader(int Xid, int Opcode);

internal readonly record struct ReplyHeader(int Xid, long Zxid, int Error);

internal readonly record struct WireStat(
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
)
{
    internal const int EncodedLength = 68;
}
