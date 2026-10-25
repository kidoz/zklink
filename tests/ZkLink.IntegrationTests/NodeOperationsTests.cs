using System.Security.Cryptography;

namespace ZkLink.IntegrationTests;

// These are required live tests: the matrix runner supplies configuration, and missing
// configuration fails initialization. Ordinary server-free jobs run their own projects.
public sealed class NodeOperationsTests : IAsyncLifetime
{
    private static readonly CreateOptions OwnerCreate = new([
        new(ZooKeeperPermissions.All, "auth", ""),
    ]);
    private readonly string _root = "/zklink-tests-" + Guid.NewGuid().ToString("N");
    private readonly string _user = "zklink-" + Guid.NewGuid().ToString("N");
    private readonly string _password = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
    private string _connection = "";
    private ZooKeeperClient? _client;
    private bool _created;
    private ZooKeeperClient Client => _client!;

    public async ValueTask InitializeAsync()
    {
        _connection =
            Environment.GetEnvironmentVariable("ZKLINK_TEST_CONNECTION_STRING")
            ?? throw new InvalidOperationException(
                "Run just zk-operations-matrix to supply the required isolated live ensemble."
            );
        string? version = Environment.GetEnvironmentVariable("ZKLINK_TEST_SERVER_VERSION");
        if (version is not ("3.8.7" or "3.9.6"))
            throw new InvalidOperationException(
                "The live runner must verify and supply a pinned server version."
            );
        _client = await ConnectAsync().ConfigureAwait(false);
        await Client
            .CreateAsync(_root, null, OwnerCreate, TestContext.Current.CancellationToken)
            .ConfigureAwait(false);
        _created = true;
    }

    public async ValueTask DisposeAsync()
    {
        if (_client is null)
            return;
        try
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            if (_created)
                await DeleteTreeAsync(_root, cleanup.Token).ConfigureAwait(false);
        }
        finally
        {
            await _client.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task<ZooKeeperClient> ConnectAsync(
        bool authenticated = true,
        string? chroot = null
    )
    {
        using var credentials = ZooKeeperAuthentication.Digest(_user, _password);
        var options = new ZooKeeperClientOptions
        {
            ConnectionString = _connection,
            Chroot = chroot,
            Tls = ZooKeeperTlsOptions.Disabled(),
            AllowUnsafePlaintextDigest = true,
            Authentication = authenticated ? [credentials] : [],
            InitialConnectTimeout = TimeSpan.FromSeconds(5),
            ConnectAttemptTimeout = TimeSpan.FromSeconds(2),
            OperationTimeout = TimeSpan.FromSeconds(5),
            CloseTimeout = TimeSpan.FromSeconds(2),
        };
        return await ZooKeeperClient
            .ConnectAsync(options, TestContext.Current.CancellationToken)
            .ConfigureAwait(false);
    }

    [Fact(Timeout = 60000)]
    [Trait("Acceptance", "T15")]
    public async Task CrudPreservesNullEmptyAndLargeDataWithStatAndChildren()
    {
        string path = _root + "/data";
        CreateResult created = await Client
            .CreateAsync(path, null, OwnerCreate, TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        Assert.Equal(path, created.Path);
        Assert.Equal(0, created.Stat.Version);
        Assert.True(created.Stat.Czxid > 0);
        Assert.Null(
            (
                await Client
                    .GetDataAsync(path, TestContext.Current.CancellationToken)
                    .ConfigureAwait(true)
            ).Data
        );
        Assert.Equal(
            created.Stat,
            await Client
                .ExistsAsync(path, TestContext.Current.CancellationToken)
                .ConfigureAwait(true)
        );
        ZNodeStat emptyStat = await Client
            .SetDataAsync(
                path,
                ReadOnlyMemory<byte>.Empty,
                ZNodeVersion.Exact(0),
                TestContext.Current.CancellationToken
            )
            .ConfigureAwait(true);
        ZNodeData empty = await Client
            .GetDataAsync(path, TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        Assert.True(empty.Data.HasValue);
        Assert.Empty(empty.Data.Value.ToArray());
        Assert.Equal(1, emptyStat.Version);
        byte[] large = new byte[1_000_000];
        Array.Fill(large, (byte)42);
        Task<ZNodeStat> writing = Client.SetDataAsync(
            path,
            large,
            ZNodeVersion.Exact(1),
            TestContext.Current.CancellationToken
        );
        Array.Fill(large, (byte)99);
        ZNodeStat updated = await writing.ConfigureAwait(true);
        ZNodeData read = await Client
            .GetDataAsync(path, TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        Assert.Equal(2, updated.Version);
        Assert.Equal(updated, read.Stat);
        Assert.Equal(large.Length, read.Stat.DataLength);
        Assert.True(read.Data!.Value.Span.ContainsAnyExcept((byte)42) == false);
        ZNodeChildren children = await Client
            .GetChildrenAsync(_root, TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        Assert.Single(children.Children, "data");
        Assert.Equal(1, children.Stat.NumChildren);
        Assert.Equal(0, children.Stat.Version);
        Assert.Equal(1, children.Stat.Cversion);
        await Client.SyncAsync(path, TestContext.Current.CancellationToken).ConfigureAwait(true);
        await Client
            .DeleteAsync(
                path,
                ZNodeVersion.Exact(updated.Version),
                TestContext.Current.CancellationToken
            )
            .ConfigureAwait(true);
        Assert.Null(
            await Client
                .ExistsAsync(path, TestContext.Current.CancellationToken)
                .ConfigureAwait(true)
        );
    }

    [Fact(Timeout = 60000)]
    [Trait("Acceptance", "T15")]
    public async Task MissingNodesAndVersionConflictsRemainExplicitServerRejections()
    {
        string path = _root + "/missing";
        Assert.Null(
            await Client
                .ExistsAsync(path, TestContext.Current.CancellationToken)
                .ConfigureAwait(true)
        );
        await RejectedAsync(
                () => Client.GetDataAsync(path, TestContext.Current.CancellationToken),
                ZooKeeperErrorCode.NoNode
            )
            .ConfigureAwait(true);
        await RejectedAsync(
                () => Client.GetChildrenAsync(path, TestContext.Current.CancellationToken),
                ZooKeeperErrorCode.NoNode
            )
            .ConfigureAwait(true);
        await RejectedAsync(
                () => Client.GetAclAsync(path, TestContext.Current.CancellationToken),
                ZooKeeperErrorCode.NoNode
            )
            .ConfigureAwait(true);
        await RejectedAsync(
                () =>
                    Client.SetDataAsync(
                        path,
                        null,
                        ZNodeVersion.Any,
                        TestContext.Current.CancellationToken
                    ),
                ZooKeeperErrorCode.NoNode
            )
            .ConfigureAwait(true);
        await RejectedAsync(
                () =>
                    Client.SetAclAsync(
                        path,
                        OwnerCreate.Acl,
                        ZNodeVersion.Any,
                        TestContext.Current.CancellationToken
                    ),
                ZooKeeperErrorCode.NoNode
            )
            .ConfigureAwait(true);
        await RejectedAsync(
                () =>
                    Client.DeleteAsync(
                        path,
                        ZNodeVersion.Any,
                        TestContext.Current.CancellationToken
                    ),
                ZooKeeperErrorCode.NoNode
            )
            .ConfigureAwait(true);
        await Client
            .CreateAsync(path, null, OwnerCreate, TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        await RejectedAsync(
                () =>
                    Client.CreateAsync(
                        path,
                        null,
                        OwnerCreate,
                        TestContext.Current.CancellationToken
                    ),
                ZooKeeperErrorCode.NodeExists
            )
            .ConfigureAwait(true);
        await RejectedAsync(
                () =>
                    Client.SetDataAsync(
                        path,
                        null,
                        ZNodeVersion.Exact(1),
                        TestContext.Current.CancellationToken
                    ),
                ZooKeeperErrorCode.BadVersion
            )
            .ConfigureAwait(true);
        await RejectedAsync(
                () =>
                    Client.DeleteAsync(
                        path,
                        ZNodeVersion.Exact(1),
                        TestContext.Current.CancellationToken
                    ),
                ZooKeeperErrorCode.BadVersion
            )
            .ConfigureAwait(true);
        await RejectedAsync(
                () =>
                    Client.SetAclAsync(
                        path,
                        OwnerCreate.Acl,
                        ZNodeVersion.Exact(1),
                        TestContext.Current.CancellationToken
                    ),
                ZooKeeperErrorCode.BadVersion
            )
            .ConfigureAwait(true);
        Assert.Equal(
            0,
            (
                await Client
                    .GetDataAsync(path, TestContext.Current.CancellationToken)
                    .ConfigureAwait(true)
            )
                .Stat
                .Version
        );
        Assert.Equal(ZooKeeperSessionState.Connected, Client.Session.State);
    }

    [Theory(Timeout = 60000)]
    [InlineData(CreateMode.Persistent)]
    [InlineData(CreateMode.Ephemeral)]
    [InlineData(CreateMode.PersistentSequential)]
    [InlineData(CreateMode.EphemeralSequential)]
    [InlineData(CreateMode.Container)]
    [Trait("Acceptance", "T16")]
    public async Task CreateModesReturnActualPathStatAndSessionOwner(CreateMode mode)
    {
        string prefix = _root + "/node-";
        CreateResult result = await Client
            .CreateAsync(
                prefix,
                null,
                new CreateOptions(OwnerCreate.Acl, mode),
                TestContext.Current.CancellationToken
            )
            .ConfigureAwait(true);
        bool sequential = mode is CreateMode.PersistentSequential or CreateMode.EphemeralSequential;
        if (sequential)
        {
            Assert.StartsWith(prefix, result.Path, StringComparison.Ordinal);
            Assert.Equal(10, result.Path.Length - prefix.Length);
            Assert.All(
                result.Path[prefix.Length..],
                character => Assert.InRange(character, '0', '9')
            );
        }
        else
            Assert.Equal(prefix, result.Path);
        if (mode is CreateMode.Ephemeral or CreateMode.EphemeralSequential)
            Assert.Equal(Client.Session.SessionId, result.Stat.EphemeralOwner);
        if (mode != CreateMode.Container) // Empty containers may already have been removed by the server.
            Assert.Equal(
                result.Stat,
                await Client
                    .ExistsAsync(result.Path, TestContext.Current.CancellationToken)
                    .ConfigureAwait(true)
            );
        Assert.True(result.Stat.Czxid > 0);
    }

    [Fact(Timeout = 60000)]
    [Trait("Acceptance", "T16")]
    public async Task TreeOperationsNeverCreateParentsOrDeleteChildrenImplicitly()
    {
        await RejectedAsync(
                () =>
                    Client.CreateAsync(
                        _root + "/absent/child",
                        null,
                        OwnerCreate,
                        TestContext.Current.CancellationToken
                    ),
                ZooKeeperErrorCode.NoNode
            )
            .ConfigureAwait(true);
        Assert.Null(
            await Client
                .ExistsAsync(_root + "/absent", TestContext.Current.CancellationToken)
                .ConfigureAwait(true)
        );
        string ephemeral = _root + "/ephemeral";
        await Client
            .CreateAsync(
                ephemeral,
                null,
                new CreateOptions(OwnerCreate.Acl, CreateMode.Ephemeral),
                TestContext.Current.CancellationToken
            )
            .ConfigureAwait(true);
        await RejectedAsync(
                () =>
                    Client.CreateAsync(
                        ephemeral + "/child",
                        null,
                        OwnerCreate,
                        TestContext.Current.CancellationToken
                    ),
                ZooKeeperErrorCode.NoChildrenForEphemerals
            )
            .ConfigureAwait(true);
        await Client
            .CreateAsync(
                _root + "/parent",
                null,
                OwnerCreate,
                TestContext.Current.CancellationToken
            )
            .ConfigureAwait(true);
        await Client
            .CreateAsync(
                _root + "/parent/child",
                null,
                OwnerCreate,
                TestContext.Current.CancellationToken
            )
            .ConfigureAwait(true);
        await RejectedAsync(
                () =>
                    Client.DeleteAsync(
                        _root + "/parent",
                        ZNodeVersion.Any,
                        TestContext.Current.CancellationToken
                    ),
                ZooKeeperErrorCode.NotEmpty
            )
            .ConfigureAwait(true);
        Assert.NotNull(
            await Client
                .ExistsAsync(_root + "/parent/child", TestContext.Current.CancellationToken)
                .ConfigureAwait(true)
        );
    }

    [Fact(Timeout = 60000)]
    [Trait("Acceptance", "T15")]
    public async Task ChrootTranslatesEveryNodeAndAclOperationAndSequentialResult()
    {
        ZooKeeperClient scoped = await ConnectAsync(chroot: _root).ConfigureAwait(true);
        await using var scopedLifetime = scoped.ConfigureAwait(true);
        Assert.NotNull(
            await scoped
                .ExistsAsync("/", TestContext.Current.CancellationToken)
                .ConfigureAwait(true)
        );
        CreateResult created = await scoped
            .CreateAsync(
                "/child",
                new byte[] { 1 },
                OwnerCreate,
                TestContext.Current.CancellationToken
            )
            .ConfigureAwait(true);
        Assert.Equal("/child", created.Path);
        Assert.NotNull(
            await Client
                .ExistsAsync(_root + "/child", TestContext.Current.CancellationToken)
                .ConfigureAwait(true)
        );
        await scoped
            .SetDataAsync(
                "/child",
                ReadOnlyMemory<byte>.Empty,
                ZNodeVersion.Any,
                TestContext.Current.CancellationToken
            )
            .ConfigureAwait(true);
        Assert.Empty(
            (
                await scoped
                    .GetDataAsync("/child", TestContext.Current.CancellationToken)
                    .ConfigureAwait(true)
            ).Data!.Value.ToArray()
        );
        ZNodeAcl acl = await scoped
            .GetAclAsync("/child", TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        await scoped
            .SetAclAsync(
                "/child",
                OwnerCreate.Acl,
                ZNodeVersion.Exact(acl.Stat.Aversion),
                TestContext.Current.CancellationToken
            )
            .ConfigureAwait(true);
        CreateResult sequential = await scoped
            .CreateAsync(
                "/prefix-",
                null,
                new CreateOptions(OwnerCreate.Acl, CreateMode.PersistentSequential),
                TestContext.Current.CancellationToken
            )
            .ConfigureAwait(true);
        Assert.StartsWith("/prefix-", sequential.Path, StringComparison.Ordinal);
        Assert.DoesNotContain(_root, sequential.Path, StringComparison.Ordinal);
        ZNodeChildren children = await scoped
            .GetChildrenAsync("/", TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        Assert.Contains("child", children.Children);
        Assert.All(children.Children, name => Assert.DoesNotContain('/', name));
        await scoped.SyncAsync("/", TestContext.Current.CancellationToken).ConfigureAwait(true);
        await scoped
            .DeleteAsync("/child", ZNodeVersion.Any, TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        Assert.Null(
            await Client
                .ExistsAsync(_root + "/child", TestContext.Current.CancellationToken)
                .ConfigureAwait(true)
        );
        await scoped.CloseAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
    }

    [Fact(Timeout = 60000)]
    [Trait("Acceptance", "T19")]
    public async Task AclReadWriteAndAdminRightsAreIndependentAndVersionsStayDistinct()
    {
        string path = _root + "/acl";
        ZooKeeperAcl owner = Assert.Single(
            (
                await Client
                    .GetAclAsync(_root, TestContext.Current.CancellationToken)
                    .ConfigureAwait(true)
            ).Acl
        );
        Assert.Equal("digest", owner.Scheme); // The server expanded the authenticated creator ACL.
        await Client
            .CreateAsync(
                path,
                null,
                new CreateOptions([owner, new(ZooKeeperPermissions.Read, "world", "anyone")]),
                TestContext.Current.CancellationToken
            )
            .ConfigureAwait(true);
        ZooKeeperClient guest = await ConnectAsync(authenticated: false).ConfigureAwait(true);
        await using var guestLifetime = guest.ConfigureAwait(true);
        await guest.GetDataAsync(path, TestContext.Current.CancellationToken).ConfigureAwait(true);
        await RejectedAsync(
                () =>
                    guest.SetDataAsync(
                        path,
                        null,
                        ZNodeVersion.Any,
                        TestContext.Current.CancellationToken
                    ),
                ZooKeeperErrorCode.NoAuth
            )
            .ConfigureAwait(true);
        ZNodeAcl masked = await guest
            .GetAclAsync(path, TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        ZooKeeperAcl maskedDigest = Assert.Single(masked.Acl, entry => entry.Scheme == "digest");
        bool digestMasked = maskedDigest.Id.EndsWith(":x", StringComparison.Ordinal);
        Assert.True(digestMasked, "A read-only caller must receive a masked digest identity.");

        await RejectedAsync(
                () =>
                    guest.SetAclAsync(
                        path,
                        [ZooKeeperAcl.OpenUnsafe],
                        ZNodeVersion.Any,
                        TestContext.Current.CancellationToken
                    ),
                ZooKeeperErrorCode.NoAuth
            )
            .ConfigureAwait(true);
        ZNodeStat aclStat = await Client
            .SetAclAsync(
                path,
                [owner, new(ZooKeeperPermissions.Write, "world", "anyone")],
                ZNodeVersion.Exact(0),
                TestContext.Current.CancellationToken
            )
            .ConfigureAwait(true);
        Assert.Equal(1, aclStat.Aversion);
        Assert.Equal(0, aclStat.Version);
        ZNodeStat dataStat = await guest
            .SetDataAsync(
                path,
                ReadOnlyMemory<byte>.Empty,
                ZNodeVersion.Exact(0),
                TestContext.Current.CancellationToken
            )
            .ConfigureAwait(true);
        Assert.Equal(1, dataStat.Version);
        Assert.Equal(1, dataStat.Aversion);
        Assert.Equal(0, dataStat.Cversion);
        await RejectedAsync(
                () => guest.GetDataAsync(path, TestContext.Current.CancellationToken),
                ZooKeeperErrorCode.NoAuth
            )
            .ConfigureAwait(true);
        Assert.Equal(ZooKeeperSessionState.Connected, guest.Session.State);
        Assert.Equal(
            2,
            (
                await guest
                    .SetDataAsync(
                        path,
                        null,
                        ZNodeVersion.Exact(1),
                        TestContext.Current.CancellationToken
                    )
                    .ConfigureAwait(true)
            ).Version
        );
        await RejectedAsync(
                () => guest.GetAclAsync(path, TestContext.Current.CancellationToken),
                ZooKeeperErrorCode.NoAuth
            )
            .ConfigureAwait(true);
        await Client
            .SetAclAsync(
                path,
                [owner, new(ZooKeeperPermissions.Admin, "world", "anyone")],
                ZNodeVersion.Exact(1),
                TestContext.Current.CancellationToken
            )
            .ConfigureAwait(true);
        ZNodeStat adminChange = await guest
            .SetAclAsync(
                path,
                [owner, new(ZooKeeperPermissions.Read, "world", "anyone")],
                ZNodeVersion.Exact(2),
                TestContext.Current.CancellationToken
            )
            .ConfigureAwait(true);
        Assert.Equal(3, adminChange.Aversion);
        await RejectedAsync(
                () =>
                    guest.SetDataAsync(
                        path,
                        null,
                        ZNodeVersion.Any,
                        TestContext.Current.CancellationToken
                    ),
                ZooKeeperErrorCode.NoAuth
            )
            .ConfigureAwait(true);
        await guest.CloseAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
    }

    [Fact(Timeout = 60000)]
    [Trait("Acceptance", "T19")]
    public async Task CreateAndDeleteRightsApplyToTheParentAndAclDoesNotInherit()
    {
        string parent = _root + "/parent";
        ZooKeeperAcl owner = Assert.Single(
            (
                await Client
                    .GetAclAsync(_root, TestContext.Current.CancellationToken)
                    .ConfigureAwait(true)
            ).Acl
        );
        await Client
            .CreateAsync(
                parent,
                null,
                new CreateOptions([owner, new(ZooKeeperPermissions.Create, "world", "anyone")]),
                TestContext.Current.CancellationToken
            )
            .ConfigureAwait(true);
        ZooKeeperClient guest = await ConnectAsync(authenticated: false).ConfigureAwait(true);
        await using var guestLifetime = guest.ConfigureAwait(true);
        await guest
            .CreateAsync(
                parent + "/child",
                null,
                new CreateOptions([ZooKeeperAcl.OpenUnsafe]),
                TestContext.Current.CancellationToken
            )
            .ConfigureAwait(true);
        await guest
            .GetDataAsync(parent + "/child", TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        await RejectedAsync(
                () =>
                    guest.DeleteAsync(
                        parent + "/child",
                        ZNodeVersion.Any,
                        TestContext.Current.CancellationToken
                    ),
                ZooKeeperErrorCode.NoAuth
            )
            .ConfigureAwait(true);
        await Client
            .SetAclAsync(
                parent,
                [owner, new(ZooKeeperPermissions.Delete, "world", "anyone")],
                ZNodeVersion.Any,
                TestContext.Current.CancellationToken
            )
            .ConfigureAwait(true);
        await guest
            .DeleteAsync(parent + "/child", ZNodeVersion.Any, TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        await RejectedAsync(
                () =>
                    guest.CreateAsync(
                        parent + "/another",
                        null,
                        new CreateOptions([ZooKeeperAcl.OpenUnsafe]),
                        TestContext.Current.CancellationToken
                    ),
                ZooKeeperErrorCode.NoAuth
            )
            .ConfigureAwait(true);
        await guest.CloseAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
    }

    [Fact(Timeout = 60000)]
    [Trait("Acceptance", "T16")]
    public async Task AcknowledgedCloseRemovesSessionEphemerals()
    {
        ZooKeeperClient owner = await ConnectAsync().ConfigureAwait(true);
        await using var ownerLifetime = owner.ConfigureAwait(true);
        string path = _root + "/ephemeral";
        CreateResult created = await owner
            .CreateAsync(
                path,
                null,
                new CreateOptions(OwnerCreate.Acl, CreateMode.Ephemeral),
                TestContext.Current.CancellationToken
            )
            .ConfigureAwait(true);
        Assert.Equal(owner.Session.SessionId, created.Stat.EphemeralOwner);
        await Client.SyncAsync(_root, TestContext.Current.CancellationToken).ConfigureAwait(true);
        Assert.NotNull(
            await Client
                .ExistsAsync(path, TestContext.Current.CancellationToken)
                .ConfigureAwait(true)
        );
        await owner.CloseAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
        Assert.Equal(ZooKeeperCloseStatus.Acknowledged, owner.Session.CloseStatus);
        await Client.SyncAsync(_root, TestContext.Current.CancellationToken).ConfigureAwait(true);
        Assert.Null(
            await Client
                .ExistsAsync(path, TestContext.Current.CancellationToken)
                .ConfigureAwait(true)
        );
    }

    [Fact(Timeout = 60000)]
    [Trait("Acceptance", "T19")]
    public async Task InvalidAclIsReportedWithoutEndingTheSession()
    {
        var invalid = new CreateOptions([
            new(ZooKeeperPermissions.All, "zklink-unknown-provider", "opaque"),
        ]);
        await RejectedAsync(
                () =>
                    Client.CreateAsync(
                        _root + "/invalid",
                        null,
                        invalid,
                        TestContext.Current.CancellationToken
                    ),
                ZooKeeperErrorCode.InvalidAcl
            )
            .ConfigureAwait(true);
        Assert.Null(
            await Client
                .ExistsAsync(_root + "/invalid", TestContext.Current.CancellationToken)
                .ConfigureAwait(true)
        );
        CreateResult created = await Client
            .CreateAsync(_root + "/valid", null, OwnerCreate, TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        Assert.Equal(0, created.Stat.Version);
    }

    private static async Task RejectedAsync(Func<Task> action, ZooKeeperErrorCode code)
    {
        ZooKeeperException error = await Assert
            .ThrowsAsync<ZooKeeperException>(action)
            .ConfigureAwait(false);
        Assert.Equal(code, error.KnownErrorCode);
        Assert.Equal(RequestOutcome.Rejected, error.Outcome);
        Assert.NotNull(error.Request.RequestId);
    }

    private async Task DeleteTreeAsync(string path, CancellationToken token)
    {
        try
        {
            ZNodeChildren children = await Client
                .GetChildrenAsync(path, token)
                .ConfigureAwait(false);
            foreach (string child in children.Children)
                await DeleteTreeAsync(path + "/" + child, token).ConfigureAwait(false);
            await Client.DeleteAsync(path, ZNodeVersion.Any, token).ConfigureAwait(false);
        }
        catch (ZooKeeperException error) when (error.KnownErrorCode == ZooKeeperErrorCode.NoNode)
        { }
    }
}
