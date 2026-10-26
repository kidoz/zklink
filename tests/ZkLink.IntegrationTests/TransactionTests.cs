namespace ZkLink.IntegrationTests;

public sealed class TransactionTests : IAsyncLifetime
{
    private readonly LiveZooKeeperFixture _fixture = new();
    private static CreateOptions OwnerCreate => LiveZooKeeperFixture.OwnerCreate;
    private ZooKeeperClient Client => _fixture.Client;
    private string Root => _fixture.Root;
    private string ConnectionString => _fixture.ConnectionString;

    public ValueTask InitializeAsync() => _fixture.InitializeAsync();

    public ValueTask DisposeAsync() => _fixture.DisposeAsync();

    private Task<ZooKeeperClient> ConnectAsync(
        bool authenticated = true,
        string? chroot = null,
        string? connectionString = null
    ) => _fixture.ConnectAsync(authenticated, chroot, connectionString);

    [Fact(Timeout = 60000)]
    [Trait("Acceptance", "T18")]
    public async Task AtomicWriteReturnsOrderedTypedResultsAndCanReferToEarlierCreates()
    {
        await Client
            .CreateAsync(Root + "/delete", null, OwnerCreate, TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        IReadOnlyList<ZooKeeperWriteResult> results = await Client
            .MultiAsync(
                [
                    ZooKeeperWriteOperation.Create(Root + "/node", null, OwnerCreate),
                    ZooKeeperWriteOperation.SetData(
                        Root + "/node",
                        new byte[] { 1, 2 },
                        ZNodeVersion.Exact(0)
                    ),
                    ZooKeeperWriteOperation.Check(Root + "/node", ZNodeVersion.Exact(1)),
                    ZooKeeperWriteOperation.Delete(Root + "/delete", ZNodeVersion.Exact(0)),
                ],
                TestContext.Current.CancellationToken
            )
            .ConfigureAwait(true);
        Assert.Equal(4, results.Count);
        CreateResult created = Assert.IsType<ZooKeeperCreatedResult>(results[0]).Value;
        ZNodeStat updated = Assert.IsType<ZooKeeperDataSetResult>(results[1]).Stat;
        Assert.IsType<ZooKeeperCheckedResult>(results[2]);
        Assert.IsType<ZooKeeperDeletedResult>(results[3]);
        Assert.Equal(Root + "/node", created.Path);
        Assert.Equal(0, created.Stat.Version);
        Assert.Equal(1, updated.Version);
        Assert.Equal(created.Stat.Czxid, updated.Mzxid);
        Assert.Equal(
            updated,
            (
                await Client
                    .GetDataAsync(created.Path, TestContext.Current.CancellationToken)
                    .ConfigureAwait(true)
            ).Stat
        );
        Assert.Null(
            await Client
                .ExistsAsync(Root + "/delete", TestContext.Current.CancellationToken)
                .ConfigureAwait(true)
        );
    }

    [Theory(Timeout = 60000)]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [Trait("Acceptance", "T18")]
    public async Task FailureAtEveryPositionRollsBackWritesAndPreservesCompleteDiagnostics(
        int failedIndex
    )
    {
        string path = Root + "/existing";
        await Client
            .CreateAsync(path, new byte[] { 7 }, OwnerCreate, TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        var operations = new List<ZooKeeperWriteOperation>
        {
            ZooKeeperWriteOperation.Create(Root + "/new", null, OwnerCreate),
            ZooKeeperWriteOperation.SetData(path, new byte[] { 9 }, ZNodeVersion.Exact(0)),
        };
        operations.Insert(failedIndex, ZooKeeperWriteOperation.Check(path, ZNodeVersion.Exact(42)));
        ZooKeeperMultiException error = await Assert
            .ThrowsAsync<ZooKeeperMultiException>(() =>
                Client.MultiAsync(operations, TestContext.Current.CancellationToken)
            )
            .ConfigureAwait(true);
        Assert.Equal(RequestOutcome.Rejected, error.Outcome);
        Assert.Equal(ZooKeeperErrorCode.BadVersion, error.KnownErrorCode);
        Assert.Equal(failedIndex, error.FailedMemberIndex);
        Assert.Equal(3, error.Diagnostics.Count);
        Assert.Equal(ZooKeeperOperation.Multi, error.Request.Operation);
        Assert.NotNull(error.Request.RequestId);
        for (var i = 0; i < error.Diagnostics.Count; i++)
        {
            Assert.Equal(i, error.Diagnostics[i].Index);
            Assert.Equal(-1, error.Diagnostics[i].Opcode);
            Assert.Equal(
                i < failedIndex ? 0
                    : i == failedIndex ? -103
                    : -2,
                error.Diagnostics[i].ErrorCode
            );
        }
        Assert.Null(
            await Client
                .ExistsAsync(Root + "/new", TestContext.Current.CancellationToken)
                .ConfigureAwait(true)
        );
        ZNodeData unchanged = await Client
            .GetDataAsync(path, TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        Assert.Equal(0, unchanged.Stat.Version);
        Assert.Equal((byte)7, Assert.Single(unchanged.Data!.Value.ToArray()));
    }

    [Fact(Timeout = 60000)]
    [Trait("Acceptance", "T18")]
    public async Task MultiReadKeepsDataChildrenAndMemberErrorsWithoutInventingParentStat()
    {
        await Client
            .CreateAsync(Root + "/node", null, OwnerCreate, TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        await Client
            .CreateAsync(
                Root + "/empty",
                ReadOnlyMemory<byte>.Empty,
                OwnerCreate,
                TestContext.Current.CancellationToken
            )
            .ConfigureAwait(true);
        IReadOnlyList<ZooKeeperReadResult> results = await Client
            .MultiReadAsync(
                [
                    ZooKeeperReadOperation.GetData(Root + "/missing"),
                    ZooKeeperReadOperation.GetData(Root + "/node"),
                    ZooKeeperReadOperation.GetChildren(Root),
                    ZooKeeperReadOperation.GetData(Root + "/empty"),
                ],
                TestContext.Current.CancellationToken
            )
            .ConfigureAwait(true);
        Assert.Equal(
            ZooKeeperErrorCode.NoNode,
            Assert.IsType<ZooKeeperReadErrorResult>(results[0]).KnownErrorCode
        );
        Assert.Null(Assert.IsType<ZooKeeperDataReadResult>(results[1]).Value.Data);
        IReadOnlyList<string> children = Assert
            .IsType<ZooKeeperChildrenReadResult>(results[2])
            .Value;
        Assert.Contains("node", children);
        Assert.Contains("empty", children);
        Assert.Empty(
            Assert.IsType<ZooKeeperDataReadResult>(results[3]).Value.Data!.Value.ToArray()
        );
    }

    [Fact(Timeout = 60000)]
    [Trait("Acceptance", "T18")]
    public async Task ChrootTranslatesEachMemberAndSequentialCreateResults()
    {
        ZooKeeperClient scoped = await ConnectAsync(chroot: Root).ConfigureAwait(true);
        await using var scopedLifetime = scoped.ConfigureAwait(true);
        IReadOnlyList<ZooKeeperWriteResult> written = await scoped
            .MultiAsync(
                [
                    ZooKeeperWriteOperation.Create("/node", null, OwnerCreate),
                    ZooKeeperWriteOperation.Create(
                        "/prefix-",
                        null,
                        new CreateOptions(OwnerCreate.Acl, CreateMode.PersistentSequential)
                    ),
                    ZooKeeperWriteOperation.SetData(
                        "/node",
                        ReadOnlyMemory<byte>.Empty,
                        ZNodeVersion.Exact(0)
                    ),
                ],
                TestContext.Current.CancellationToken
            )
            .ConfigureAwait(true);
        Assert.Equal("/node", Assert.IsType<ZooKeeperCreatedResult>(written[0]).Value.Path);
        string sequence = Assert.IsType<ZooKeeperCreatedResult>(written[1]).Value.Path;
        Assert.StartsWith("/prefix-", sequence, StringComparison.Ordinal);
        Assert.DoesNotContain(Root, sequence, StringComparison.Ordinal);
        IReadOnlyList<ZooKeeperReadResult> read = await scoped
            .MultiReadAsync(
                [ZooKeeperReadOperation.GetData("/node"), ZooKeeperReadOperation.GetChildren("/")],
                TestContext.Current.CancellationToken
            )
            .ConfigureAwait(true);
        Assert.Equal(1, Assert.IsType<ZooKeeperDataReadResult>(read[0]).Value.Stat.Version);
        Assert.Contains(sequence[1..], Assert.IsType<ZooKeeperChildrenReadResult>(read[1]).Value);
        await scoped.CloseAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
    }

    [Theory(Timeout = 60000)]
    [InlineData(CreateMode.Ephemeral)]
    [InlineData(CreateMode.EphemeralSequential)]
    [InlineData(CreateMode.Container)]
    [Trait("Acceptance", "T18")]
    public async Task ExtendedCreateModesReturnNormalizedCreateResults(CreateMode mode)
    {
        IReadOnlyList<ZooKeeperWriteResult> results = await Client
            .MultiAsync(
                [
                    ZooKeeperWriteOperation.Create(
                        Root + "/mode-",
                        null,
                        new CreateOptions(OwnerCreate.Acl, mode)
                    ),
                ],
                TestContext.Current.CancellationToken
            )
            .ConfigureAwait(true);
        CreateResult created = Assert.IsType<ZooKeeperCreatedResult>(Assert.Single(results)).Value;
        Assert.True(created.Stat.Czxid > 0);
        if (mode is CreateMode.Ephemeral or CreateMode.EphemeralSequential)
            Assert.Equal(Client.Session.SessionId, created.Stat.EphemeralOwner);
        if (mode == CreateMode.EphemeralSequential)
            Assert.Equal((Root + "/mode-").Length + 10, created.Path.Length);
        else
            Assert.Equal(Root + "/mode-", created.Path);
    }

    [Fact(Timeout = 60000)]
    [Trait("Acceptance", "T18")]
    public async Task ReadPermissionErrorsDoNotDiscardOtherMembersOrEndTheSession()
    {
        await Client
            .CreateAsync(
                Root + "/private",
                null,
                OwnerCreate,
                TestContext.Current.CancellationToken
            )
            .ConfigureAwait(true);
        await Client
            .CreateAsync(
                Root + "/public",
                ReadOnlyMemory<byte>.Empty,
                new CreateOptions([ZooKeeperAcl.OpenUnsafe]),
                TestContext.Current.CancellationToken
            )
            .ConfigureAwait(true);
        ZooKeeperClient guest = await ConnectAsync(authenticated: false).ConfigureAwait(true);
        await using var guestLifetime = guest.ConfigureAwait(true);
        IReadOnlyList<ZooKeeperReadResult> results = await guest
            .MultiReadAsync(
                [
                    ZooKeeperReadOperation.GetData(Root + "/private"),
                    ZooKeeperReadOperation.GetData(Root + "/public"),
                ],
                TestContext.Current.CancellationToken
            )
            .ConfigureAwait(true);
        Assert.Equal(
            ZooKeeperErrorCode.NoAuth,
            Assert.IsType<ZooKeeperReadErrorResult>(results[0]).KnownErrorCode
        );
        Assert.Empty(
            Assert.IsType<ZooKeeperDataReadResult>(results[1]).Value.Data!.Value.ToArray()
        );
        Assert.Equal(ZooKeeperSessionState.Connected, guest.Session.State);
        await guest.CloseAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
    }

    [Fact(Timeout = 60000)]
    [Trait("Acceptance", "T13")]
    [Trait("Acceptance", "T18")]
    public async Task CommittedTransactionWithDroppedReplyIsUnknownAndNeverReplayedAfterResume()
    {
        string existing = Root + "/existing";
        await Client
            .CreateAsync(existing, null, OwnerCreate, TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        var proxy = new DropTransactionReplyProxy(
            ConnectionString,
            TestContext.Current.CancellationToken
        );
        await using var proxyLifetime = proxy.ConfigureAwait(true);
        ZooKeeperClient faulted = await ConnectAsync(connectionString: proxy.Endpoint)
            .ConfigureAwait(true);
        await using var faultedLifetime = faulted.ConfigureAwait(true);
        long originalSession = faulted.Session.SessionId;
        Task<IReadOnlyList<ZooKeeperWriteResult>> transaction = faulted.MultiAsync(
            [
                ZooKeeperWriteOperation.SetData(existing, new byte[] { 8 }, ZNodeVersion.Exact(0)),
                ZooKeeperWriteOperation.Create(
                    Root + "/sequence-",
                    null,
                    new CreateOptions(OwnerCreate.Acl, CreateMode.PersistentSequential)
                ),
            ],
            TestContext.Current.CancellationToken
        );
        await proxy
            .Dropped.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        ZooKeeperException error = await Assert
            .ThrowsAsync<ZooKeeperException>(() => transaction)
            .ConfigureAwait(true);
        Assert.Equal(RequestOutcome.Unknown, error.Outcome);
        Assert.Equal(ZooKeeperOperation.Multi, error.Request.Operation);
        await faulted
            .WaitForConnectionAsync(TestContext.Current.CancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        Assert.Equal(originalSession, faulted.Session.SessionId);
        Assert.Equal(2, faulted.Session.ConnectionGeneration);
        await Client.SyncAsync(Root, TestContext.Current.CancellationToken).ConfigureAwait(true);
        Assert.Equal(
            1,
            (
                await Client
                    .GetDataAsync(existing, TestContext.Current.CancellationToken)
                    .ConfigureAwait(true)
            )
                .Stat
                .Version
        );
        Assert.Single(
            (
                await Client
                    .GetChildrenAsync(Root, TestContext.Current.CancellationToken)
                    .ConfigureAwait(true)
            ).Children,
            name => name.StartsWith("sequence-", StringComparison.Ordinal)
        );
        // A new ordinary read proves the resumed connection is usable without replaying the write.
        await faulted
            .GetDataAsync(existing, TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        Assert.Equal(1, proxy.ForwardedTransactions);
        await faulted.CloseAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
    }
}
