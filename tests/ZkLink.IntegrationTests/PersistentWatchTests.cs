namespace ZkLink.IntegrationTests;

public sealed class PersistentWatchTests : IAsyncLifetime
{
    private const int TestTimeoutMilliseconds = 60_000;
    private static readonly TimeSpan EventGuard = TimeSpan.FromSeconds(15);
    private readonly LiveZooKeeperFixture _fixture = new();
    private ZooKeeperClient Client => _fixture.Client;
    private string Root => _fixture.Root;
    private static CreateOptions OwnerCreate => LiveZooKeeperFixture.OwnerCreate;

    public ValueTask InitializeAsync() => _fixture.InitializeAsync();

    public ValueTask DisposeAsync() => _fixture.DisposeAsync();

    [Fact(Timeout = TestTimeoutMilliseconds)]
    [Trait("Acceptance", "T25")]
    public async Task ExactWatchSurvivesDeletionRecreationAndRepeatedDataAndChildChanges()
    {
        string path = Root + "/node";
        var watch = await Client
            .AddWatchAsync(
                path,
                PersistentWatchMode.Persistent,
                TestContext.Current.CancellationToken
            )
            .ConfigureAwait(true);
        var reader = watch
            .ReadEventsAsync(TestContext.Current.CancellationToken)
            .GetAsyncEnumerator(TestContext.Current.CancellationToken);
        await using var lifetime = reader.ConfigureAwait(true);
        await Client
            .CreateAsync(path, null, OwnerCreate, TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        await ExpectAsync(reader, watch, ZooKeeperWatchEventKind.NodeCreated, path)
            .ConfigureAwait(true);
        await Client
            .SetDataAsync(
                path,
                new byte[] { 1 },
                ZNodeVersion.Any,
                TestContext.Current.CancellationToken
            )
            .ConfigureAwait(true);
        await ExpectAsync(reader, watch, ZooKeeperWatchEventKind.NodeDataChanged, path)
            .ConfigureAwait(true);
        await Client
            .CreateAsync(path + "/child", null, OwnerCreate, TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        await ExpectAsync(reader, watch, ZooKeeperWatchEventKind.NodeChildrenChanged, path)
            .ConfigureAwait(true);
        await Client
            .DeleteAsync(path + "/child", ZNodeVersion.Any, TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        await ExpectAsync(reader, watch, ZooKeeperWatchEventKind.NodeChildrenChanged, path)
            .ConfigureAwait(true);
        await Client
            .DeleteAsync(path, ZNodeVersion.Any, TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        await ExpectAsync(reader, watch, ZooKeeperWatchEventKind.NodeDeleted, path)
            .ConfigureAwait(true);
        await Client
            .CreateAsync(path, null, OwnerCreate, TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        await ExpectAsync(reader, watch, ZooKeeperWatchEventKind.NodeCreated, path)
            .ConfigureAwait(true);
        var removed = await watch
            .RemoveAsync(WatchRemovalMode.ServerConfirmed, TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        Assert.Equal(WatchRemovalStatus.ServerConfirmed, removed.Status);
        Assert.False(await reader.MoveNextAsync().ConfigureAwait(true));
    }

    [Fact(Timeout = TestTimeoutMilliseconds)]
    [Trait("Acceptance", "T25")]
    public async Task RecursiveWatchFiltersDescendantChildDuplicatesAndPreservesOneShotSubscribers()
    {
        var recursive = await Client
            .AddWatchAsync(
                Root,
                PersistentWatchMode.PersistentRecursive,
                TestContext.Current.CancellationToken
            )
            .ConfigureAwait(true);
        var reader = recursive
            .ReadEventsAsync(TestContext.Current.CancellationToken)
            .GetAsyncEnumerator(TestContext.Current.CancellationToken);
        await using var lifetime = reader.ConfigureAwait(true);
        string path = Root + "/child";
        await Client
            .CreateAsync(path, null, OwnerCreate, TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        await ExpectAsync(reader, recursive, ZooKeeperWatchEventKind.NodeCreated, path)
            .ConfigureAwait(true);
        var child = await Client
            .GetChildrenAndWatchAsync(path, TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        var childReader = child
            .Watch.ReadEventsAsync(TestContext.Current.CancellationToken)
            .GetAsyncEnumerator(TestContext.Current.CancellationToken);
        await using var childLifetime = childReader.ConfigureAwait(true);
        await Client
            .CreateAsync(path + "/nested", null, OwnerCreate, TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        await ExpectAsync(reader, recursive, ZooKeeperWatchEventKind.NodeCreated, path + "/nested")
            .ConfigureAwait(true);
        await ExpectAsync(
                childReader,
                child.Watch,
                ZooKeeperWatchEventKind.NodeChildrenChanged,
                path
            )
            .ConfigureAwait(true);
        Assert.False(await childReader.MoveNextAsync().ConfigureAwait(true));
        await Client
            .SetDataAsync(
                path + "/nested",
                new byte[] { 1 },
                ZNodeVersion.Any,
                TestContext.Current.CancellationToken
            )
            .ConfigureAwait(true);
        await ExpectAsync(
                reader,
                recursive,
                ZooKeeperWatchEventKind.NodeDataChanged,
                path + "/nested"
            )
            .ConfigureAwait(true);
        await Client
            .DeleteAsync(path + "/nested", ZNodeVersion.Any, TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        await ExpectAsync(reader, recursive, ZooKeeperWatchEventKind.NodeDeleted, path + "/nested")
            .ConfigureAwait(true);
        await recursive.DisposeAsync().ConfigureAwait(true);
        Assert.False(await reader.MoveNextAsync().ConfigureAwait(true));
    }

    [Theory(Timeout = TestTimeoutMilliseconds)]
    [InlineData(PersistentWatchMode.Persistent)]
    [InlineData(PersistentWatchMode.PersistentRecursive)]
    [Trait("Acceptance", "T29")]
    public async Task SharedRemovalPreservesTheOtherHandleAndIndependentCategory(
        PersistentWatchMode mode
    )
    {
        var first = await Client
            .AddWatchAsync(Root, mode, TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        var second = await Client
            .AddWatchAsync(Root, mode, TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        ZooKeeperWatch? other = null;
        var otherMode =
            mode == PersistentWatchMode.Persistent
                ? PersistentWatchMode.PersistentRecursive
                : PersistentWatchMode.Persistent;
        if (_fixture.CompatibilityProfile == ZooKeeperCompatibilityProfile.ZooKeeper39)
            other = await Client
                .AddWatchAsync(Root, otherMode, TestContext.Current.CancellationToken)
                .ConfigureAwait(true);
        else
        {
            Assert.Throws<NotSupportedException>(() =>
            {
                _ = Client.AddWatchAsync(Root, otherMode, TestContext.Current.CancellationToken);
            });
            Assert.Throws<NotSupportedException>(() =>
            {
                _ = Client.ExistsAndWatchAsync(Root, TestContext.Current.CancellationToken);
            });
        }
        var retained = await first
            .RemoveAsync(WatchRemovalMode.ServerConfirmed, TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        Assert.Equal(WatchRemovalStatus.SharedRegistrationRetained, retained.Status);
        var removed = await second
            .RemoveAsync(WatchRemovalMode.ServerConfirmed, TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        Assert.Equal(WatchRemovalStatus.ServerConfirmed, removed.Status);
        var repeated = await second
            .RemoveAsync(WatchRemovalMode.ServerConfirmed, TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        Assert.Equal(WatchRemovalStatus.NoWatcher, repeated.Status);
        other ??= await Client
            .AddWatchAsync(Root, mode, TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        var reader = other
            .ReadEventsAsync(TestContext.Current.CancellationToken)
            .GetAsyncEnumerator(TestContext.Current.CancellationToken);
        await using var lifetime = reader.ConfigureAwait(true);
        await Client
            .SetDataAsync(Root, null, ZNodeVersion.Any, TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        await ExpectAsync(reader, other, ZooKeeperWatchEventKind.NodeDataChanged, Root)
            .ConfigureAwait(true);
        var firstReader = first
            .ReadEventsAsync(TestContext.Current.CancellationToken)
            .GetAsyncEnumerator(TestContext.Current.CancellationToken);
        await using var firstLifetime = firstReader.ConfigureAwait(true);
        Assert.False(await firstReader.MoveNextAsync().ConfigureAwait(true));
    }

    [Fact(Timeout = TestTimeoutMilliseconds)]
    public async Task ChrootRootRecursiveWatchReportsRelativeDescendantsAndCanBeReregisteredAfterDisposal()
    {
        var scoped = await _fixture.ConnectAsync(chroot: Root).ConfigureAwait(true);
        await using var scopedLifetime = scoped.ConfigureAwait(true);
        var old = await scoped
            .AddWatchAsync(
                "/",
                PersistentWatchMode.PersistentRecursive,
                TestContext.Current.CancellationToken
            )
            .ConfigureAwait(true);
        await old.DisposeAsync().ConfigureAwait(true);
        var watch = await scoped
            .AddWatchAsync(
                "/",
                PersistentWatchMode.PersistentRecursive,
                TestContext.Current.CancellationToken
            )
            .ConfigureAwait(true);
        var reader = watch
            .ReadEventsAsync(TestContext.Current.CancellationToken)
            .GetAsyncEnumerator(TestContext.Current.CancellationToken);
        await using var lifetime = reader.ConfigureAwait(true);
        await Client
            .CreateAsync(Root + "/child", null, OwnerCreate, TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        await ExpectAsync(reader, watch, ZooKeeperWatchEventKind.NodeCreated, "/child")
            .ConfigureAwait(true);
        await scoped.CloseAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
        Assert.False(await reader.MoveNextAsync().ConfigureAwait(true));
    }

    [Theory(Timeout = TestTimeoutMilliseconds)]
    [InlineData(PersistentWatchMode.Persistent)]
    [InlineData(PersistentWatchMode.PersistentRecursive)]
    [Trait("Acceptance", "T26")]
    public async Task ReconnectReportsGapForMissedChangesThenContinuesOnTheSameSession(
        PersistentWatchMode mode
    )
    {
        var proxy = new WatchReconnectProxy(
            _fixture.ConnectionString,
            TestContext.Current.CancellationToken
        );
        await using var proxyLifetime = proxy.ConfigureAwait(true);
        var owner = await _fixture
            .ConnectAsync(connectionString: proxy.Endpoint)
            .ConfigureAwait(true);
        await using var ownerLifetime = owner.ConfigureAwait(true);
        string path = Root + "/absent";
        var watch = await owner
            .AddWatchAsync(path, mode, TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        var disposed = await owner
            .AddWatchAsync(Root + "/disposed", mode, TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        long sessionId = owner.Session.SessionId;
        long oldGeneration = owner.Session.ConnectionGeneration;
        var states = owner.SubscribeStateChanges();
        await using var statesLifetime = states.ConfigureAwait(true);
        var stateReader = states
            .ReadStatesAsync(TestContext.Current.CancellationToken)
            .GetAsyncEnumerator(TestContext.Current.CancellationToken);
        await using var stateLifetime = stateReader.ConfigureAwait(true);
        Assert.True(await stateReader.MoveNextAsync().ConfigureAwait(true));
        proxy.DropAndPause();
        do
        {
            Assert.True(
                await stateReader
                    .MoveNextAsync()
                    .AsTask()
                    .WaitAsync(EventGuard, TestContext.Current.CancellationToken)
                    .ConfigureAwait(true)
            );
        } while (stateReader.Current.State != ZooKeeperSessionState.Disconnected);
        await disposed.DisposeAsync().ConfigureAwait(true);
        // No event log can reconstruct this absent -> present -> absent interval.
        await Client
            .CreateAsync(path, null, OwnerCreate, TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        await Client
            .DeleteAsync(path, ZNodeVersion.Any, TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        proxy.Resume();
        await owner
            .WaitForConnectionAsync(TestContext.Current.CancellationToken)
            .WaitAsync(EventGuard, TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        Assert.Equal(sessionId, owner.Session.SessionId);
        Assert.True(owner.Session.ConnectionGeneration > oldGeneration);
        var reader = watch
            .ReadEventsAsync(TestContext.Current.CancellationToken)
            .GetAsyncEnumerator(TestContext.Current.CancellationToken);
        await using var lifetime = reader.ConfigureAwait(true);
        await ExpectAsync(reader, watch, ZooKeeperWatchEventKind.ReconnectedWithGap, path)
            .ConfigureAwait(true);
        Assert.Null(reader.Current.Zxid);
        Assert.Equal(owner.Session.ConnectionGeneration, reader.Current.ConnectionGeneration);
        Assert.Null(
            await owner
                .ExistsAsync(path, TestContext.Current.CancellationToken)
                .ConfigureAwait(true)
        );
        await Client
            .CreateAsync(path, null, OwnerCreate, TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        await ExpectAsync(reader, watch, ZooKeeperWatchEventKind.NodeCreated, path)
            .ConfigureAwait(true);
        await Client
            .SetDataAsync(
                path,
                new byte[] { 1 },
                ZNodeVersion.Any,
                TestContext.Current.CancellationToken
            )
            .ConfigureAwait(true);
        await ExpectAsync(reader, watch, ZooKeeperWatchEventKind.NodeDataChanged, path)
            .ConfigureAwait(true);
        var disposedReader = disposed
            .ReadEventsAsync(TestContext.Current.CancellationToken)
            .GetAsyncEnumerator(TestContext.Current.CancellationToken);
        await using var disposedLifetime = disposedReader.ConfigureAwait(true);
        Assert.False(await disposedReader.MoveNextAsync().ConfigureAwait(true));
    }

    private static async Task ExpectAsync(
        IAsyncEnumerator<ZooKeeperWatchEvent> reader,
        ZooKeeperWatch watch,
        ZooKeeperWatchEventKind kind,
        string path
    )
    {
        Assert.True(
            await reader
                .MoveNextAsync()
                .AsTask()
                .WaitAsync(EventGuard, TestContext.Current.CancellationToken)
                .ConfigureAwait(false)
        );
        Assert.Equal(kind, reader.Current.Kind);
        Assert.Equal(path, reader.Current.Path);
        Assert.Equal(watch.RegistrationId, reader.Current.RegistrationId);
    }
}
