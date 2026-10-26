using ZkLink.Protocol;

namespace ZkLink.Protocol.Tests;

internal sealed record ProtocolFixture(
    string Name,
    int Xid,
    WireRequest Request,
    WireResult Result
);

internal static class ProtocolFixtures
{
    internal static readonly ProtocolLimits Limits = new();
    internal static readonly WireStat Stat = new(1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11);
    internal static readonly byte[] Data = [0, 255, 42];
    internal static readonly WireAcl[] Acl = [new(31, "world", "anyone")];
    internal static readonly string[] Children = ["a", "Ж"];
    internal static readonly WireRequest[] Writes =
    [
        new CreateRequest(1, "/node", Data, Acl, 0),
        new CreateRequest(15, "/node", Data, Acl, 0),
        new CreateRequest(19, "/node", Data, Acl, 4),
        new CreateRequest(21, "/node", Data, Acl, 5, 1234),
        new SetDataRequest("/node", Data, -1),
        new VersionRequest(2, "/node", -1),
        new VersionRequest(13, "/node", -1),
    ];
    internal static readonly WireRequest[] Reads =
    [
        new ReadRequest(4, "/node", false),
        new ReadRequest(8, "/node", false),
    ];

    internal static readonly ProtocolFixture[] All = Build();

    internal static byte[] Bytes(string name) =>
        Convert.FromHexString(
            File.ReadAllText(
                    Path.Combine(AppContext.BaseDirectory, "Fixtures", "Protocol", name + ".hex")
                )
                .Trim()
        );

    private static ProtocolFixture[] Build() =>
        [
            new("create", 1, Writes[0], new PathResult("/node")),
            new("delete", 1, new VersionRequest(2, "/node", -1), new EmptyResult()),
            new("exists", 1, new ReadRequest(3, "/node", true), new StatResult(Stat)),
            new("get-data", 1, Reads[0], new DataResult(Data, Stat)),
            new("get-null-data", 1, Reads[0], new DataResult(null, Stat)),
            new("get-empty-data", 1, Reads[0], new DataResult([], Stat)),
            new(
                "set-data",
                1,
                new SetDataRequest("/node", Data, int.MinValue),
                new StatResult(Stat)
            ),
            new(
                "get-acl",
                1,
                new PathRequest(ZooKeeperOperation.GetAcl, "/node"),
                new AclResult(Acl, Stat)
            ),
            new("set-acl", 1, new SetAclRequest("/node", Acl, -1), new StatResult(Stat)),
            new("get-children", 1, Reads[1], new StringsResult(Children)),
            new(
                "sync",
                1,
                new PathRequest(ZooKeeperOperation.Sync, "/node"),
                new PathResult("/node")
            ),
            new("ping", -2, new EmptyRequest(11), new EmptyResult()),
            new(
                "get-children-stat",
                1,
                new ReadRequest(12, "/node", true),
                new StringsResult(Children, Stat)
            ),
            new("check", 1, new VersionRequest(13, "/node", -1), new EmptyResult()),
            new("create-stat", 1, Writes[1], new PathResult("/node", Stat)),
            new(
                "create-ephemeral",
                1,
                new CreateRequest(15, "/node", Data, Acl, 1),
                new PathResult("/node", Stat)
            ),
            new(
                "create-sequential",
                1,
                new CreateRequest(15, "/node", Data, Acl, 2),
                new PathResult("/node0000000001", Stat)
            ),
            new(
                "create-ephemeral-sequential",
                1,
                new CreateRequest(15, "/node", Data, Acl, 3),
                new PathResult("/node0000000001", Stat)
            ),
            new(
                "create-sequential-prefix",
                1,
                new CreateRequest(15, "/node/", Data, Acl, 2),
                new PathResult("/node/0000000001", Stat)
            ),
            new("check-watches", 1, new WatchRequest(17, "/node", 3), new EmptyResult()),
            new("remove-watches", 1, new WatchRequest(18, "/node", 4), new EmptyResult()),
            new("create-container", 1, Writes[2], new PathResult("/node", Stat)),
            new("create-ttl", 1, Writes[3], new PathResult("/node", Stat)),
            new(
                "create-ttl-sequential",
                1,
                new CreateRequest(21, "/node", Data, Acl, 6, CreateOptions.MaxTtlMilliseconds),
                new PathResult("/node0000000001", Stat)
            ),
            new("auth", -4, new AuthRequest("digest", Data), new EmptyResult()),
            new(
                "restore",
                -8,
                new RestoreRequest(12, ["/data"], ["/exists"], ["/children"]),
                new EmptyResult()
            ),
            new(
                "get-ephemerals",
                1,
                new PathRequest(ZooKeeperOperation.GetEphemerals, "/node"),
                new StringsResult(["/node", "/node2"])
            ),
            new(
                "count-children",
                1,
                new PathRequest(ZooKeeperOperation.GetAllChildrenNumber, "/node"),
                new CountResult(123)
            ),
            new(
                "restore-persistent",
                -8,
                new RestoreRequest(
                    12,
                    ["/data"],
                    ["/exists"],
                    ["/children"],
                    ["/persistent"],
                    ["/recursive"]
                ),
                new EmptyResult()
            ),
            new("add-watch", 1, new WatchRequest(106, "/node", 1), new EmptyResult()),
            new(
                "who-am-i",
                1,
                new EmptyRequest(107),
                new IdentityResult([new ZooKeeperClientIdentity("digest", "fixture-user")])
            ),
            new("close", 1, new EmptyRequest(-11), new EmptyResult()),
            new(
                "multi-write",
                1,
                new MultiRequest(false, Writes),
                new MultiResult([
                    new(1, 0, new PathResult("/node")),
                    new(15, 0, new PathResult("/node", Stat)),
                    new(15, 0, new PathResult("/node", Stat)),
                    new(15, 0, new PathResult("/node", Stat)),
                    new(5, 0, new StatResult(Stat)),
                    new(2, 0, new EmptyResult()),
                    new(13, 0, new EmptyResult()),
                ])
            ),
            new(
                "multi-failed",
                1,
                new MultiRequest(false, Writes),
                new MultiResult([
                    new(-1, 0, new ErrorResult(0)),
                    new(-1, -103, new ErrorResult(-103)),
                    new(-1, -2, new ErrorResult(-2)),
                    new(-1, -2, new ErrorResult(-2)),
                    new(-1, -2, new ErrorResult(-2)),
                    new(-1, -2, new ErrorResult(-2)),
                    new(-1, -2, new ErrorResult(-2)),
                ])
            ),
            new(
                "multi-read",
                1,
                new MultiRequest(true, Reads),
                new MultiResult([
                    new(4, 0, new DataResult(Data, Stat)),
                    new(8, 0, new StringsResult(Children)),
                ])
            ),
            new(
                "multi-read-error",
                1,
                new MultiRequest(true, Reads),
                new MultiResult([
                    new(-1, -101, new ErrorResult(-101)),
                    new(8, 0, new StringsResult(Children)),
                ])
            ),
        ];

    internal static void AssertResult(WireResult expected, WireResult actual)
    {
        Assert.Equal(expected.GetType(), actual.GetType());
        switch (expected, actual)
        {
            case (DataResult e, DataResult a):
                Assert.Equal(e.Data, a.Data);
                Assert.Equal(e.Stat, a.Stat);
                break;
            case (StringsResult e, StringsResult a):
                Assert.Equal(e.Values, a.Values);
                Assert.Equal(e.Stat, a.Stat);
                break;
            case (AclResult e, AclResult a):
                Assert.Equal(e.Acl, a.Acl);
                Assert.Equal(e.Stat, a.Stat);
                break;
            case (IdentityResult e, IdentityResult a):
                Assert.Equal(e.Identities.Length, a.Identities.Length);
                for (var i = 0; i < e.Identities.Length; i++)
                {
                    Assert.Equal(e.Identities[i].Scheme, a.Identities[i].Scheme);
                    Assert.Equal(e.Identities[i].User, a.Identities[i].User);
                }
                break;
            case (MultiResult e, MultiResult a):
                Assert.Equal(e.Members.Length, a.Members.Length);
                for (var i = 0; i < e.Members.Length; i++)
                {
                    Assert.Equal(e.Members[i].Opcode, a.Members[i].Opcode);
                    Assert.Equal(e.Members[i].Error, a.Members[i].Error);
                    AssertResult(e.Members[i].Result, a.Members[i].Result);
                }
                break;
            default:
                Assert.Equal(expected, actual);
                break;
        }
    }

    internal static void Emit(string name, ReadOnlySpan<byte> bytes)
    {
        var root = Environment.GetEnvironmentVariable("ZKLINK_ORACLE_OUTPUT");
        if (root is null)
        {
            return;
        }
        var directory = Path.Combine(root, "Protocol");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, name + ".hex"), Convert.ToHexString(bytes));
    }
}
