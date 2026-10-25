using ZkLink.Configuration;
using ZkLink.Protocol;

namespace ZkLink.Protocol.Tests;

public sealed class ConfigurationTests
{
    [Theory]
    [InlineData("host", 1, "host", 2181, null)]
    [InlineData("127.0.0.1:2281/app", 1, "127.0.0.1", 2281, "/app")]
    [InlineData("[::1]", 1, "::1", 2181, null)]
    [InlineData("[2001:db8::1]:2281,zk.example/app", 2, "2001:db8::1", 2281, "/app")]
    [InlineData("host/", 1, "host", 2181, null)]
    [Trait("Acceptance", "T05")]
    public void ParseHostsPortsIpv6AndChroot(
        string connection,
        int count,
        string firstHost,
        int firstPort,
        string? chroot
    )
    {
        ParsedConnectionString parsed = ConnectionStringParser.Parse(connection);
        Assert.Equal(count, parsed.Endpoints.Count);
        Assert.Equal(firstHost, parsed.Endpoints[0].Host);
        Assert.Equal(firstPort, parsed.Endpoints[0].Port);
        Assert.Equal(chroot, parsed.Chroot);
        Assert.Throws<NotSupportedException>(() =>
            ((IList<ZooKeeperEndpoint>)parsed.Endpoints).Clear()
        );
    }

    [Theory]
    [InlineData("")]
    [InlineData(",host")]
    [InlineData("host,")]
    [InlineData("host,,other")]
    [InlineData("host:")]
    [InlineData("host:0")]
    [InlineData("host:65536")]
    [InlineData("host:+2181")]
    [InlineData("host: 2181")]
    [InlineData("user:secret@host")]
    [InlineData("::1")]
    [InlineData("[::1")]
    [InlineData("[host]")]
    [InlineData("[::1]tail")]
    [InlineData("[::1]:")]
    [InlineData("host/app/")]
    [InlineData("host//app")]
    [Trait("Acceptance", "T05")]
    public void MalformedAndAmbiguousConnectionsAreRejected(string value) =>
        Assert.ThrowsAny<ArgumentException>(() => ConnectionStringParser.Parse(value));

    [Fact]
    public void ChrootSettingsCannotConflictAndEndpointCountsAreBounded()
    {
        Assert.Equal("/app", ConnectionStringParser.Parse("host", "/app").Chroot);
        Assert.Equal("/app", ConnectionStringParser.Parse("host/app", "/app").Chroot);
        Assert.Null(ConnectionStringParser.Parse("host/", "/").Chroot);
        Assert.Throws<ArgumentException>(() => ConnectionStringParser.Parse("host/app", "/other"));
        Assert.Throws<ArgumentException>(() => ConnectionStringParser.Parse("host/", "/app"));
        Assert.Throws<ArgumentException>(() =>
            ConnectionStringParser.Parse(string.Join(',', Enumerable.Repeat("host", 1025)))
        );
    }

    [Fact]
    [Trait("Acceptance", "T05")]
    public void EveryUtf16CodeUnitMatchesApachePathUtils()
    {
        byte[] expected = ProtocolFixtures.Bytes("path-code-units");
        Assert.Equal(8192, expected.Length);
        for (int code = 0; code <= char.MaxValue; code++)
        {
            string path = "/a" + (char)code + "b";
            bool valid = (expected[code / 8] & (1 << (code % 8))) != 0;
            if (valid)
                ZNodePath.Validate(path);
            else
                Assert.ThrowsAny<ArgumentException>(() => ZNodePath.Validate(path));
        }
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/a")]
    [InlineData("/a..b")]
    [InlineData("/A %2F\\é")]
    public void PathsArePreservedWithoutFilesystemOrUriNormalization(string path)
    {
        ZNodePath.Validate(path);
        Assert.Equal(path, new ChrootPath(null).ToServer(path));
    }

    [Theory]
    [InlineData("relative")]
    [InlineData("//")]
    [InlineData("/a//b")]
    [InlineData("/.")]
    [InlineData("/..")]
    [InlineData("/a/./b")]
    [InlineData("/a/../b")]
    [InlineData("/a/")]
    public void InvalidOrdinaryPathsFail(string path) =>
        Assert.ThrowsAny<ArgumentException>(() => ZNodePath.Validate(path));

    [Theory]
    [InlineData("/")]
    [InlineData("/a/")]
    [InlineData("/.")]
    [InlineData("/..")]
    [InlineData("/a/.")]
    public void SequentialPrefixesAreValidatedWithTheFutureSuffix(string path) =>
        ZNodePath.Validate(path, sequential: true);

    [Theory]
    [InlineData("//")]
    [InlineData("/a//")]
    [InlineData("/a/../")]
    public void SequentialSuffixDoesNotRepairIntermediateComponents(string path) =>
        Assert.ThrowsAny<ArgumentException>(() => ZNodePath.Validate(path, sequential: true));

    [Fact]
    [Trait("Acceptance", "T06")]
    public void EveryNamespacedRequestAndMultiMemberIsTranslatedOnce()
    {
        var paths = new ChrootPath("/app");
        foreach (ProtocolFixture fixture in ProtocolFixtures.All)
        {
            WireRequest actual = NamespaceTranslator.ToServer(fixture.Request, paths);
            string? path = actual switch
            {
                PathRequest p => p.Path,
                ReadRequest p => p.Path,
                VersionRequest p => p.Path,
                SetDataRequest p => p.Path,
                CreateRequest p => p.Path,
                SetAclRequest p => p.Path,
                WatchRequest p => p.Path,
                _ => null,
            };
            if (path is not null)
                Assert.Equal(
                    fixture.Name == "create-sequential-prefix" ? "/app/node/" : "/app/node",
                    path
                );
            if (actual is MultiRequest multi)
                foreach (WireRequest member in multi.Members)
                    Assert.Equal(
                        "/app/node",
                        member switch
                        {
                            ReadRequest p => p.Path,
                            VersionRequest p => p.Path,
                            SetDataRequest p => p.Path,
                            CreateRequest p => p.Path,
                            _ => throw new InvalidOperationException(),
                        }
                    );
            if (actual is RestoreRequest restore)
            {
                Assert.Equal("/app/data", restore.Data[0]);
                Assert.Equal("/app/exists", restore.Exists[0]);
                Assert.Equal("/app/children", restore.Children[0]);
                if (restore.Persistent is not null)
                {
                    Assert.Equal("/app/persistent", restore.Persistent[0]);
                    Assert.Equal("/app/recursive", restore.Recursive![0]);
                }
                Assert.Equal(12, restore.Zxid);
            }
        }
        Assert.Equal("/app", paths.ToServer("/"));
        Assert.Equal("/app/", paths.ToServer("/", sequential: true));
        // A client-relative path that happens to start with the chroot is still prefixed.
        Assert.Equal("/app/app/node", paths.ToServer("/app/node"));
    }

    [Fact]
    [Trait("Acceptance", "T06")]
    public void ResultsNotificationsAndEphemeralListsHonorComponentBoundaries()
    {
        var paths = new ChrootPath("/app");
        Assert.Equal("/", paths.ToClient("/app"));
        Assert.Equal("/node", paths.ToClient("/app/node"));
        Assert.Throws<InvalidDataException>(() => paths.ToClient("/app2/node"));
        Assert.Equal(
            new PathResult("/node", ProtocolFixtures.Stat),
            NamespaceTranslator.ToClient(
                new PathResult("/app/node", ProtocolFixtures.Stat),
                15,
                paths
            )
        );
        var children = new StringsResult(["app", "child"]);
        Assert.Same(children, NamespaceTranslator.ToClient(children, 12, paths));
        var filtered = Assert.IsType<StringsResult>(
            NamespaceTranslator.ToClient(
                new StringsResult(["/app", "/app/node", "/app/node2", "/app2/node", "/other"]),
                103,
                paths
            )
        );
        Assert.Equal(3, filtered.Values.Length);
        Assert.Equal("/", filtered.Values[0]);
        Assert.Equal("/node2", filtered.Values[2]);
        WireNotification notification = NamespaceTranslator.ToClient(
            new WireNotification(3, 3, "/app/node", 12),
            paths
        );
        Assert.Equal("/node", notification.Path);
        Assert.Equal(12, notification.Zxid);
        var multi = new MultiResult([
            new(15, 0, new PathResult("/app/node", ProtocolFixtures.Stat)),
            new(-1, -103, new ErrorResult(-103)),
        ]);
        var translated = Assert.IsType<MultiResult>(NamespaceTranslator.ToClient(multi, 14, paths));
        Assert.Equal("/node", Assert.IsType<PathResult>(translated.Members[0].Result).Path);
        Assert.Equal(new ErrorResult(-103), translated.Members[1].Result);
    }
}
