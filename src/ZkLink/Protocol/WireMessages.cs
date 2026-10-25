namespace ZkLink.Protocol;

// Internal closed request shapes, never public arbitrary opcode bodies.
internal abstract record WireRequest(int Opcode)
{
    private protected WireRequest()
        : this(0) { }

    public sealed override string ToString() => "ZooKeeper wire request";
}

internal sealed record EmptyRequest(int Type) : WireRequest(Type);

internal sealed record PathRequest(int Type, string Path) : WireRequest(Type);

internal sealed record ReadRequest(int Type, string Path, bool Watch) : WireRequest(Type);

internal sealed record VersionRequest(int Type, string Path, int Version) : WireRequest(Type);

internal sealed record SetDataRequest(string Path, ReadOnlyMemory<byte>? Data, int Version)
    : WireRequest(5);

internal sealed record WireAcl(int Permissions, string Scheme, string Id)
{
    public override string ToString() => "ZooKeeper wire ACL";
}

internal sealed record CreateRequest(
    int Type,
    string Path,
    ReadOnlyMemory<byte>? Data,
    IReadOnlyList<WireAcl> Acl,
    int Flags,
    long? Ttl = null
) : WireRequest(Type);

internal sealed record SetAclRequest(string Path, IReadOnlyList<WireAcl> Acl, int Version)
    : WireRequest(7);

internal sealed record WatchRequest(int Type, string Path, int Mode) : WireRequest(Type);

internal sealed record AuthRequest(string Scheme, ReadOnlyMemory<byte> Credentials)
    : WireRequest(100);

internal sealed record RestoreRequest(
    long Zxid,
    IReadOnlyList<string?> Data,
    IReadOnlyList<string?> Exists,
    IReadOnlyList<string?> Children,
    IReadOnlyList<string?>? Persistent = null,
    IReadOnlyList<string?>? Recursive = null
) : WireRequest(Persistent is null && Recursive is null ? 101 : 105);

internal sealed record MultiRequest(bool ReadOnly, IReadOnlyList<WireRequest> Members)
    : WireRequest(ReadOnly ? 22 : 14);

internal abstract record WireResult
{
    public sealed override string ToString() => "ZooKeeper wire result";
}

internal sealed record EmptyResult : WireResult;

internal sealed record StatResult(WireStat Stat) : WireResult;

internal sealed record DataResult(byte[]? Data, WireStat Stat) : WireResult;

internal sealed record PathResult(string Path, WireStat? Stat = null) : WireResult;

internal sealed record StringsResult(string[] Values, WireStat? Stat = null) : WireResult;

internal sealed record AclResult(WireAcl[] Acl, WireStat Stat) : WireResult;

internal sealed record CountResult(int Count) : WireResult;

internal sealed record IdentityResult(ZooKeeperClientIdentity[] Identities) : WireResult;

internal sealed record ErrorResult(int Error) : WireResult;

internal sealed record MultiResult(WireMultiMember[] Members) : WireResult;

internal sealed record WireMultiMember(int Opcode, int Error, WireResult Result);

internal sealed record WireReply(ReplyHeader Header, WireResult Result);

internal sealed record WireNotification(int Type, int State, string? Path, long? Zxid)
{
    internal bool IsRecognized =>
        (Type == -1 || Type is >= 1 and <= 7)
        && State is -112 or -1 or 0 or 1 or 3 or 4 or 5 or 6 or 7;

    public override string ToString() => "ZooKeeper wire notification";
}
