namespace ZkLink.IntegrationTests;

[Trait("Fixture", "Observer")]
public sealed class ObserverEndpointTests : IAsyncLifetime
{
    private const int TestDeadlineMilliseconds = 60_000;
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(15);
    private readonly LiveZooKeeperFixture _fixture = new();

    public ValueTask InitializeAsync() => _fixture.InitializeAsync();

    public ValueTask DisposeAsync() => _fixture.DisposeAsync();

    [Fact(Timeout = TestDeadlineMilliseconds)]
    [Trait("Acceptance", "T32")]
    public async Task ObserverForwardsWritesAndRestoresTheSameSessionAndWatches()
    {
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken
        );
        bounded.CancelAfter(Guard);
        var token = bounded.Token;
        string endpoint =
            Environment.GetEnvironmentVariable("ZKLINK_TEST_OBSERVER_ENDPOINT")
            ?? throw new InvalidOperationException("The owned observer fixture is required.");
        var proxy = new WatchReconnectProxy(endpoint, inspectFrames: false, token);
        await using var proxyLifetime = proxy.ConfigureAwait(true);
        var owner = await _fixture
            .ConnectAsync(connectionString: proxy.Endpoint, cancellationToken: token)
            .ConfigureAwait(true);
        await using var ownerLifetime = owner.ConfigureAwait(true);
        long session = owner.Session.SessionId;
        long generation = owner.Session.ConnectionGeneration;
        string path = _fixture.Root + "/observer";
        string lease = _fixture.Root + "/lease";
        await owner
            .CreateAsync(path, null, LiveZooKeeperFixture.OwnerCreate, token)
            .ConfigureAwait(true);
        await owner
            .CreateAsync(
                lease,
                null,
                new CreateOptions(LiveZooKeeperFixture.OwnerCreate.Acl, CreateMode.Ephemeral),
                token
            )
            .ConfigureAwait(true);
        var watch = await owner
            .AddWatchAsync(path, PersistentWatchMode.Persistent, token)
            .ConfigureAwait(true);
        await using var watchLifetime = watch.ConfigureAwait(true);
        var events = watch.ReadEventsAsync(token).GetAsyncEnumerator(token);
        await using var eventLifetime = events.ConfigureAwait(true);
        var states = owner.SubscribeStateChanges();
        await using var stateLifetime = states.ConfigureAwait(true);
        var reader = states.ReadStatesAsync(token).GetAsyncEnumerator(token);
        await using var readerLifetime = reader.ConfigureAwait(true);
        Assert.True(await reader.MoveNextAsync().ConfigureAwait(true));
        proxy.DropAndPause();
        try
        {
            do
            {
                Assert.True(await reader.MoveNextAsync().ConfigureAwait(true));
            } while (reader.Current.State != ZooKeeperSessionState.Disconnected);
            proxy.Resume();
            await owner.WaitForConnectionAsync(token).ConfigureAwait(true);
            Assert.Equal(session, owner.Session.SessionId);
            Assert.True(owner.Session.ConnectionGeneration > generation);
            Assert.True(await events.MoveNextAsync().ConfigureAwait(true));
            Assert.Equal(ZooKeeperWatchEventKind.ReconnectedWithGap, events.Current.Kind);
            byte[] data = "observer-write"u8.ToArray();
            await owner.SetDataAsync(path, data, ZNodeVersion.Any, token).ConfigureAwait(true);
            Assert.True(await events.MoveNextAsync().ConfigureAwait(true));
            Assert.Equal(ZooKeeperWatchEventKind.NodeDataChanged, events.Current.Kind);
            Assert.Equal(
                data,
                (await owner.GetDataAsync(path, token).ConfigureAwait(true)).Data!.Value.ToArray()
            );
            Assert.Equal(
                session,
                (await owner.ExistsAsync(lease, token).ConfigureAwait(true))!.Value.EphemeralOwner
            );
            await owner.CloseAsync(token).ConfigureAwait(true);
            Assert.Equal(ZooKeeperCloseStatus.Acknowledged, owner.Session.CloseStatus);
            await _fixture.Client.SyncAsync(_fixture.Root, token).ConfigureAwait(true);
            Assert.Null(await _fixture.Client.ExistsAsync(lease, token).ConfigureAwait(true));
        }
        finally
        {
            proxy.Resume();
        }
    }
}
