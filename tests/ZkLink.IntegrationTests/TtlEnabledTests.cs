namespace ZkLink.IntegrationTests;

[Trait("Fixture", "TTL")]
public sealed class TtlEnabledTests : IAsyncLifetime
{
    private readonly LiveZooKeeperFixture _fixture = new();
    private ZooKeeperClient Client => _fixture.Client;
    private string Root => _fixture.Root;
    private static CreateOptions OwnerCreate => LiveZooKeeperFixture.OwnerCreate;

    public ValueTask InitializeAsync()
    {
        TtlTestSupport.RequireSetting("enabled");
        return _fixture.InitializeAsync();
    }

    public ValueTask DisposeAsync() => _fixture.DisposeAsync();

    [Theory(Timeout = TtlTestSupport.TestTimeoutMilliseconds)]
    [InlineData(CreateMode.PersistentWithTtl, false)]
    [InlineData(CreateMode.PersistentWithTtl, true)]
    [InlineData(CreateMode.PersistentSequentialWithTtl, false)]
    [InlineData(CreateMode.PersistentSequentialWithTtl, true)]
    [Trait("Acceptance", "T17")]
    public async Task BothCreateModesReturnChrootPathsAndStatAndSurviveSessionClose(
        CreateMode mode,
        bool multi
    )
    {
        var scoped = await _fixture
            .ConnectAsync(chroot: Root, enableTtlNodes: true)
            .ConfigureAwait(true);
        await using var lifetime = scoped.ConfigureAwait(true);
        var options = new CreateOptions(
            OwnerCreate.Acl,
            mode,
            TimeSpan.FromTicks(CreateOptions.MaxTtlMilliseconds * TimeSpan.TicksPerMillisecond)
        );
        string prefix = mode == CreateMode.PersistentWithTtl ? "/ttl" : "/seq-";
        CreateResult created;
        if (multi)
        {
            var results = await scoped
                .MultiAsync(
                    [
                        ZooKeeperWriteOperation.Create(prefix, "initial"u8.ToArray(), options),
                        ZooKeeperWriteOperation.Check("/", ZNodeVersion.Exact(0)),
                    ],
                    TestContext.Current.CancellationToken
                )
                .ConfigureAwait(true);
            created = Assert.IsType<ZooKeeperCreatedResult>(results[0]).Value;
        }
        else
            created = await scoped
                .CreateAsync(
                    prefix,
                    "initial"u8.ToArray(),
                    options,
                    TestContext.Current.CancellationToken
                )
                .ConfigureAwait(true);
        Assert.StartsWith(prefix, created.Path, StringComparison.Ordinal);
        if (mode == CreateMode.PersistentWithTtl)
            Assert.Equal(prefix, created.Path);
        else
            Assert.True(created.Path.Length > prefix.Length);
        ZNodeData observed = await scoped
            .GetDataAsync(created.Path, TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        Assert.Equal(created.Stat, observed.Stat);
        Assert.Equal("initial"u8.ToArray(), observed.Data!.Value.ToArray());
        Assert.Equal(0, created.Stat.Version);
        Assert.Empty(
            await scoped
                .GetEphemeralsAsync("/", TestContext.Current.CancellationToken)
                .ConfigureAwait(true)
        );
        await scoped.CloseAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
        await Client.SyncAsync(Root, TestContext.Current.CancellationToken).ConfigureAwait(true);
        Assert.NotNull(
            await Client
                .ExistsAsync(Root + created.Path, TestContext.Current.CancellationToken)
                .ConfigureAwait(true)
        );
        await Client
            .CreateAsync(
                Root + created.Path + "/child",
                null,
                OwnerCreate,
                TestContext.Current.CancellationToken
            )
            .ConfigureAwait(true);
    }

    [Fact(Timeout = TtlTestSupport.TestTimeoutMilliseconds)]
    [Trait("Acceptance", "T17")]
    public async Task DefaultClientRejectsTtlEvenWhenServerHasEnabledIt()
    {
        var options = TtlTestSupport.Options(CreateMode.PersistentWithTtl);
        Assert.Throws<NotSupportedException>(() =>
        {
            _ = Client.CreateAsync(
                Root + "/ttl",
                null,
                options,
                TestContext.Current.CancellationToken
            );
        });
        Assert.Throws<NotSupportedException>(() =>
        {
            _ = Client.MultiAsync(
                [
                    ZooKeeperWriteOperation.Create(Root + "/ordinary", null, OwnerCreate),
                    ZooKeeperWriteOperation.Create(Root + "/ttl", null, options),
                ],
                TestContext.Current.CancellationToken
            );
        });
        Assert.Null(
            await Client
                .ExistsAsync(Root + "/ordinary", TestContext.Current.CancellationToken)
                .ConfigureAwait(true)
        );
        Assert.Null(
            await Client
                .ExistsAsync(Root + "/ttl", TestContext.Current.CancellationToken)
                .ConfigureAwait(true)
        );
    }

    [Fact(Timeout = TtlTestSupport.TestTimeoutMilliseconds)]
    [Trait("Acceptance", "T17")]
    public async Task ChildrenProtectExpiredTtlParentUntilItBecomesEmpty()
    {
        var enabled = await _fixture.ConnectAsync(enableTtlNodes: true).ConfigureAwait(true);
        await using var lifetime = enabled.ConfigureAwait(true);
        string path = Root + "/parent";
        // One atomic create prevents cleanup between parent creation and child creation.
        await enabled
            .MultiAsync(
                [
                    ZooKeeperWriteOperation.Create(
                        path,
                        null,
                        TtlTestSupport.Options(CreateMode.PersistentWithTtl)
                    ),
                    ZooKeeperWriteOperation.Create(path + "/child", null, OwnerCreate),
                ],
                TestContext.Current.CancellationToken
            )
            .ConfigureAwait(true);
        var started = TimeProvider.System.GetTimestamp();
        while (
            TimeProvider.System.GetElapsedTime(started) < TtlTestSupport.ProtectedObservationWindow
        )
        {
            await Client
                .SyncAsync(path, TestContext.Current.CancellationToken)
                .ConfigureAwait(true);
            Assert.NotNull(
                await Client
                    .ExistsAsync(path, TestContext.Current.CancellationToken)
                    .ConfigureAwait(true)
            );
            await Task.Delay(TtlTestSupport.PollInterval, TestContext.Current.CancellationToken)
                .ConfigureAwait(true);
        }
        await Client
            .DeleteAsync(path + "/child", ZNodeVersion.Any, TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        await TtlTestSupport.WaitForDeletionAsync(Client, path).ConfigureAwait(true);
    }

    [Fact(Timeout = TtlTestSupport.TestTimeoutMilliseconds)]
    [Trait("Acceptance", "T17")]
    public async Task ContainerParentCanDisappearBeforeTheNextChildCreate()
    {
        string path = Root + "/container";
        await Client
            .CreateAsync(
                path,
                null,
                new CreateOptions(OwnerCreate.Acl, CreateMode.Container),
                TestContext.Current.CancellationToken
            )
            .ConfigureAwait(true);
        await Client
            .CreateAsync(path + "/child", null, OwnerCreate, TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        await Client
            .DeleteAsync(path + "/child", ZNodeVersion.Any, TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        await TtlTestSupport.WaitForDeletionAsync(Client, path).ConfigureAwait(true);
        var error = await Assert
            .ThrowsAsync<ZooKeeperException>(() =>
                Client.CreateAsync(
                    path + "/later",
                    null,
                    OwnerCreate,
                    TestContext.Current.CancellationToken
                )
            )
            .ConfigureAwait(true);
        Assert.Equal(ZooKeeperErrorCode.NoNode, error.KnownErrorCode);
        Assert.Equal(RequestOutcome.Rejected, error.Outcome);
    }
}

internal static class TtlTestSupport
{
    internal const int TestTimeoutMilliseconds = 60_000;
    private const int ShortTtlMilliseconds = 100;
    internal static readonly TimeSpan ProtectedObservationWindow = TimeSpan.FromSeconds(3);
    internal static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan DeletionDeadline = TimeSpan.FromSeconds(20);

    internal static CreateOptions Options(CreateMode mode) =>
        new(
            LiveZooKeeperFixture.OwnerCreate.Acl,
            mode,
            TimeSpan.FromMilliseconds(ShortTtlMilliseconds)
        );

    internal static void RequireSetting(string setting) =>
        Assert.Equal(setting, Environment.GetEnvironmentVariable("ZKLINK_TEST_TTL_SETTING"));

    internal static async Task WaitForDeletionAsync(ZooKeeperClient client, string path)
    {
        using var guard = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken
        );
        guard.CancelAfter(DeletionDeadline);
        while (true)
        {
            await client.SyncAsync(path, guard.Token).ConfigureAwait(false);
            if (await client.ExistsAsync(path, guard.Token).ConfigureAwait(false) is null)
                return;
            await Task.Delay(PollInterval, guard.Token).ConfigureAwait(false);
        }
    }
}
