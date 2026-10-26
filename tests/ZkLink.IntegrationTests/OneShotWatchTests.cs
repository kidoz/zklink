namespace ZkLink.IntegrationTests;

public sealed class OneShotWatchTests : IAsyncLifetime
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
    [Trait("Acceptance", "T23")]
    public async Task MissingExistsReturnsAnActiveWatchAndCreationDeliversOnce()
    {
        string path = Root + "/missing";
        var watched = await Client
            .ExistsAndWatchAsync(path, TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        Assert.Null(watched.Value);
        await Client
            .CreateAsync(path, null, OwnerCreate, TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        await ExpectAsync(watched.Watch, ZooKeeperWatchEventKind.NodeCreated, path)
            .ConfigureAwait(true);
        await Client
            .SetDataAsync(
                path,
                new byte[] { 1 },
                ZNodeVersion.Any,
                TestContext.Current.CancellationToken
            )
            .ConfigureAwait(true);
        Assert.Equal(ZooKeeperSessionState.Connected, Client.Session.State);
    }

    [Theory(Timeout = TestTimeoutMilliseconds)]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Acceptance", "T23")]
    public async Task DataAndPresentExistsShareOneServerWatchAndEachReceiveTheirEvent(bool delete)
    {
        string path = Root + "/data";
        await Client
            .CreateAsync(path, new byte[] { 1 }, OwnerCreate, TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        var data = await Client
            .GetDataAndWatchAsync(path, TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        var exists = await Client
            .ExistsAndWatchAsync(path, TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        Assert.NotEqual(data.Watch.RegistrationId, exists.Watch.RegistrationId);
        if (delete)
            await Client
                .DeleteAsync(path, ZNodeVersion.Any, TestContext.Current.CancellationToken)
                .ConfigureAwait(true);
        else
            await Client
                .SetDataAsync(
                    path,
                    new byte[] { 2 },
                    ZNodeVersion.Any,
                    TestContext.Current.CancellationToken
                )
                .ConfigureAwait(true);
        var kind = delete
            ? ZooKeeperWatchEventKind.NodeDeleted
            : ZooKeeperWatchEventKind.NodeDataChanged;
        await ExpectAsync(data.Watch, kind, path).ConfigureAwait(true);
        await ExpectAsync(exists.Watch, kind, path).ConfigureAwait(true);
        Assert.Equal((byte)1, Assert.Single(data.Value.Data!.Value.ToArray()));
    }

    [Fact(Timeout = TestTimeoutMilliseconds)]
    [Trait("Acceptance", "T23")]
    public async Task ChildWatchReturnsParentStatAndReportsChildChangesOrWatchedNodeDeletion()
    {
        string path = Root + "/parent";
        var created = await Client
            .CreateAsync(path, null, OwnerCreate, TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        var watched = await Client
            .GetChildrenAndWatchAsync(path, TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        Assert.Equal(created.Stat.Czxid, watched.Value.Stat.Czxid);
        Assert.Empty(watched.Value.Children);
        await Client
            .CreateAsync(path + "/child", null, OwnerCreate, TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        await ExpectAsync(watched.Watch, ZooKeeperWatchEventKind.NodeChildrenChanged, path)
            .ConfigureAwait(true);
        await Client
            .DeleteAsync(path + "/child", ZNodeVersion.Any, TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        watched = await Client
            .GetChildrenAndWatchAsync(path, TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        await Client
            .DeleteAsync(path, ZNodeVersion.Any, TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        await ExpectAsync(watched.Watch, ZooKeeperWatchEventKind.NodeDeleted, path)
            .ConfigureAwait(true);
    }

    [Fact(Timeout = TestTimeoutMilliseconds)]
    [Trait("Acceptance", "T29")]
    public async Task SharedRemovalRetainsOtherHandlesAndConfirmedRemovalReportsNoWatcherOnRepeat()
    {
        var first = await Client
            .ExistsAndWatchAsync(Root, TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        var second = await Client
            .GetDataAndWatchAsync(Root, TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        var retained = await first
            .Watch.RemoveAsync(
                WatchRemovalMode.ServerConfirmed,
                TestContext.Current.CancellationToken
            )
            .ConfigureAwait(true);
        Assert.Equal(WatchRemovalStatus.SharedRegistrationRetained, retained.Status);
        var removed = await second
            .Watch.RemoveAsync(
                WatchRemovalMode.ServerConfirmed,
                TestContext.Current.CancellationToken
            )
            .ConfigureAwait(true);
        Assert.Equal(WatchRemovalStatus.ServerConfirmed, removed.Status);
        var repeated = await second
            .Watch.RemoveAsync(
                WatchRemovalMode.ServerConfirmed,
                TestContext.Current.CancellationToken
            )
            .ConfigureAwait(true);
        Assert.Equal(WatchRemovalStatus.NoWatcher, repeated.Status);
        var next = await Client
            .ExistsAndWatchAsync(Root, TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        await Client
            .SetDataAsync(Root, null, ZNodeVersion.Any, TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        await ExpectAsync(next.Watch, ZooKeeperWatchEventKind.NodeDataChanged, Root)
            .ConfigureAwait(true);
        await ExpectClosedAsync(first.Watch).ConfigureAwait(true);
        await ExpectClosedAsync(second.Watch).ConfigureAwait(true);
    }

    [Fact(Timeout = TestTimeoutMilliseconds)]
    [Trait("Acceptance", "T29")]
    public async Task LocalDisposeCannotRemoveAWatchRegisteredAfterIt()
    {
        var first = await Client
            .ExistsAndWatchAsync(Root, TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        await first.Watch.DisposeAsync().ConfigureAwait(true);
        var second = await Client
            .ExistsAndWatchAsync(Root, TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        await Client
            .SetDataAsync(Root, null, ZNodeVersion.Any, TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        await ExpectAsync(second.Watch, ZooKeeperWatchEventKind.NodeDataChanged, Root)
            .ConfigureAwait(true);
        await ExpectClosedAsync(first.Watch).ConfigureAwait(true);
    }

    [Fact(Timeout = TestTimeoutMilliseconds)]
    public async Task ChrootAndMissingReadErrorsPreserveTheirContracts()
    {
        var scoped = await _fixture.ConnectAsync(chroot: Root).ConfigureAwait(true);
        await using var lifetime = scoped.ConfigureAwait(true);
        var error = await Assert
            .ThrowsAsync<ZooKeeperException>(() =>
                scoped.GetDataAndWatchAsync("/missing", TestContext.Current.CancellationToken)
            )
            .ConfigureAwait(true);
        Assert.Equal(ZooKeeperErrorCode.NoNode, error.KnownErrorCode);
        var watched = await scoped
            .ExistsAndWatchAsync("/missing", TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        await Client
            .CreateAsync(
                Root + "/missing",
                null,
                OwnerCreate,
                TestContext.Current.CancellationToken
            )
            .ConfigureAwait(true);
        await ExpectAsync(watched.Watch, ZooKeeperWatchEventKind.NodeCreated, "/missing")
            .ConfigureAwait(true);
    }

    [Fact(Timeout = TestTimeoutMilliseconds)]
    public async Task AcknowledgedCloseCompletesWatchStreamsWithoutANodeEvent()
    {
        var owner = await _fixture.ConnectAsync().ConfigureAwait(true);
        await using var lifetime = owner.ConfigureAwait(true);
        var watched = await owner
            .ExistsAndWatchAsync(Root, TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        await owner.CloseAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
        await ExpectClosedAsync(watched.Watch).ConfigureAwait(true);
    }

    [Fact(Timeout = TestTimeoutMilliseconds)]
    [Trait("Acceptance", "T26")]
    [Trait("Acceptance", "T29")]
    public async Task SameSessionResumeRestoresAcknowledgedWatchAndNeverRevivesDisposedHandle()
    {
        string path = Root + "/node";
        await Client
            .CreateAsync(path, null, OwnerCreate, TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        var proxy = new WatchReconnectProxy(
            _fixture.ConnectionString,
            TestContext.Current.CancellationToken
        );
        await using var proxyLifetime = proxy.ConfigureAwait(true);
        var watchedClient = await _fixture
            .ConnectAsync(connectionString: proxy.Endpoint)
            .ConfigureAwait(true);
        await using var clientLifetime = watchedClient.ConfigureAwait(true);
        var data = await watchedClient
            .GetDataAndWatchAsync(path, TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        var removed = await watchedClient
            .ExistsAndWatchAsync(Root + "/absent", TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        long sessionId = watchedClient.Session.SessionId;
        var states = watchedClient.SubscribeStateChanges();
        await using var statesLifetime = states.ConfigureAwait(true);
        var reader = states
            .ReadStatesAsync(TestContext.Current.CancellationToken)
            .GetAsyncEnumerator(TestContext.Current.CancellationToken);
        await using var readerLifetime = reader.ConfigureAwait(true);
        Assert.True(await reader.MoveNextAsync().ConfigureAwait(true));
        proxy.DropAndPause();
        do
        {
            Assert.True(
                await reader
                    .MoveNextAsync()
                    .AsTask()
                    .WaitAsync(EventGuard, TestContext.Current.CancellationToken)
                    .ConfigureAwait(true)
            );
        } while (reader.Current.State != ZooKeeperSessionState.Disconnected);
        await removed.Watch.DisposeAsync().ConfigureAwait(true);
        await Client
            .SetDataAsync(
                path,
                new byte[] { 2 },
                ZNodeVersion.Any,
                TestContext.Current.CancellationToken
            )
            .ConfigureAwait(true);
        await Client
            .CreateAsync(Root + "/absent", null, OwnerCreate, TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        proxy.Resume();
        await watchedClient
            .WaitForConnectionAsync(TestContext.Current.CancellationToken)
            .WaitAsync(EventGuard, TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        Assert.Equal(sessionId, watchedClient.Session.SessionId);
        Assert.True(watchedClient.Session.ConnectionGeneration > 1);
        await ExpectAsync(data.Watch, ZooKeeperWatchEventKind.NodeDataChanged, path)
            .ConfigureAwait(true);
        await ExpectClosedAsync(removed.Watch).ConfigureAwait(true);
        Assert.Equal(
            1,
            (
                await watchedClient
                    .GetDataAsync(path, TestContext.Current.CancellationToken)
                    .ConfigureAwait(true)
            )
                .Stat
                .Version
        );
    }

    private static async Task ExpectAsync(
        ZooKeeperWatch watch,
        ZooKeeperWatchEventKind kind,
        string path
    )
    {
        var reader = watch
            .ReadEventsAsync(TestContext.Current.CancellationToken)
            .GetAsyncEnumerator(TestContext.Current.CancellationToken);
        await using var lifetime = reader.ConfigureAwait(false);
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
        Assert.False(
            await reader
                .MoveNextAsync()
                .AsTask()
                .WaitAsync(EventGuard, TestContext.Current.CancellationToken)
                .ConfigureAwait(false)
        );
    }

    private static async Task ExpectClosedAsync(ZooKeeperWatch watch)
    {
        var reader = watch
            .ReadEventsAsync(TestContext.Current.CancellationToken)
            .GetAsyncEnumerator(TestContext.Current.CancellationToken);
        await using var lifetime = reader.ConfigureAwait(false);
        Assert.False(
            await reader
                .MoveNextAsync()
                .AsTask()
                .WaitAsync(EventGuard, TestContext.Current.CancellationToken)
                .ConfigureAwait(false)
        );
    }
}
