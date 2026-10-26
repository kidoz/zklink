namespace ZkLink.IntegrationTests;

public sealed class WatchRecoveryTests : IAsyncLifetime
{
    private const int TestTimeoutMilliseconds = 60_000;
    private static readonly TimeSpan EventGuard = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan ShortSessionTimeout = TimeSpan.FromSeconds(2);
    private readonly LiveZooKeeperFixture _fixture = new();
    private ZooKeeperClient Client => _fixture.Client;

    public ValueTask InitializeAsync() => _fixture.InitializeAsync();

    public ValueTask DisposeAsync() => _fixture.DisposeAsync();

    [Theory(Timeout = TestTimeoutMilliseconds)]
    [InlineData(PersistentWatchMode.Persistent)]
    [InlineData(PersistentWatchMode.PersistentRecursive)]
    [Trait("Acceptance", "T26")]
    [Trait("Acceptance", "T29")]
    public async Task DroppedRestoreAcknowledgmentBlocksReadinessAndRetriesWithoutRevivingDisposedWatch(
        PersistentWatchMode mode
    )
    {
        string path = _fixture.Root + "/node";
        await Client
            .CreateAsync(
                path,
                null,
                LiveZooKeeperFixture.OwnerCreate,
                TestContext.Current.CancellationToken
            )
            .ConfigureAwait(true);
        var proxy = new WatchReconnectProxy(
            _fixture.ConnectionString,
            TestContext.Current.CancellationToken
        );
        await using var proxyLifetime = proxy.ConfigureAwait(true);
        var owner = await _fixture
            .ConnectAsync(connectionString: proxy.Endpoint)
            .ConfigureAwait(true);
        await using var ownerLifetime = owner.ConfigureAwait(true);
        var watch = await owner
            .AddWatchAsync(path, mode, TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        var disposed = await owner
            .AddWatchAsync(_fixture.Root + "/disposed", mode, TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        long id = owner.Session.SessionId;
        long generation = owner.Session.ConnectionGeneration;
        var states = owner.SubscribeStateChanges();
        await using var statesLifetime = states.ConfigureAwait(true);
        var stateReader = states
            .ReadStatesAsync(TestContext.Current.CancellationToken)
            .GetAsyncEnumerator(TestContext.Current.CancellationToken);
        await using var stateLifetime = stateReader.ConfigureAwait(true);
        Assert.True(await stateReader.MoveNextAsync().ConfigureAwait(true));
        var events = watch
            .ReadEventsAsync(TestContext.Current.CancellationToken)
            .GetAsyncEnumerator(TestContext.Current.CancellationToken);
        await using var eventLifetime = events.ConfigureAwait(true);
        var nextEvent = events.MoveNextAsync().AsTask();
        proxy.HoldNextRestoreReply();
        proxy.DropAndPause();
        await WaitStateAsync(stateReader, ZooKeeperSessionState.Disconnected).ConfigureAwait(true);
        var readiness = owner.WaitForConnectionAsync(TestContext.Current.CancellationToken);
        await Client
            .SetDataAsync(
                path,
                new byte[] { 1 },
                ZNodeVersion.Any,
                TestContext.Current.CancellationToken
            )
            .ConfigureAwait(true);
        proxy.Resume();
        await proxy
            .RestoreReplyHeld.WaitAsync(EventGuard, TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        Assert.Equal(ZooKeeperSessionState.Restoring, owner.Session.State);
        Assert.False(readiness.IsCompleted);
        Assert.False(nextEvent.IsCompleted);
        var error = await Assert
            .ThrowsAsync<ZooKeeperException>(() =>
                owner.SyncAsync(path, TestContext.Current.CancellationToken)
            )
            .ConfigureAwait(true);
        Assert.Equal(RequestOutcome.NotSent, error.Outcome);
        await disposed.DisposeAsync().ConfigureAwait(true);
        // The server has applied restoration, but its acknowledgment never reaches the client.
        proxy.DropHeldRestoreReplyAndPause();
        await WaitStateAsync(stateReader, ZooKeeperSessionState.Disconnected).ConfigureAwait(true);
        Assert.False(readiness.IsCompleted);
        Assert.False(nextEvent.IsCompleted);
        await Client
            .SetDataAsync(
                path,
                new byte[] { 2 },
                ZNodeVersion.Any,
                TestContext.Current.CancellationToken
            )
            .ConfigureAwait(true);
        proxy.Resume();
        await readiness
            .WaitAsync(EventGuard, TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        Assert.Equal(id, owner.Session.SessionId);
        Assert.True(owner.Session.ConnectionGeneration >= generation + 2);
        Assert.Equal(2, proxy.RestoreReplies);
        Assert.True(
            await nextEvent
                .WaitAsync(EventGuard, TestContext.Current.CancellationToken)
                .ConfigureAwait(true)
        );
        Assert.Equal(ZooKeeperWatchEventKind.ReconnectedWithGap, events.Current.Kind);
        Assert.Equal(owner.Session.ConnectionGeneration, events.Current.ConnectionGeneration);
        Assert.Null(events.Current.Zxid);
        await Client
            .SetDataAsync(
                path,
                new byte[] { 3 },
                ZNodeVersion.Any,
                TestContext.Current.CancellationToken
            )
            .ConfigureAwait(true);
        Assert.True(
            await events
                .MoveNextAsync()
                .AsTask()
                .WaitAsync(EventGuard, TestContext.Current.CancellationToken)
                .ConfigureAwait(true)
        );
        Assert.Equal(ZooKeeperWatchEventKind.NodeDataChanged, events.Current.Kind);
        var removedEvents = disposed
            .ReadEventsAsync(TestContext.Current.CancellationToken)
            .GetAsyncEnumerator(TestContext.Current.CancellationToken);
        await using var removedLifetime = removedEvents.ConfigureAwait(true);
        Assert.False(await removedEvents.MoveNextAsync().ConfigureAwait(true));
        Assert.Equal(
            3,
            (
                await owner
                    .GetDataAsync(path, TestContext.Current.CancellationToken)
                    .ConfigureAwait(true)
            )
                .Stat
                .Version
        );
        await owner.CloseAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
        Assert.False(await events.MoveNextAsync().ConfigureAwait(true));
        Assert.Equal(ZooKeeperCloseStatus.Acknowledged, owner.Session.CloseStatus);
    }

    [Theory(Timeout = TestTimeoutMilliseconds)]
    [InlineData(PersistentWatchMode.Persistent)]
    [InlineData(PersistentWatchMode.PersistentRecursive)]
    [Trait("Acceptance", "T09")]
    public async Task PausedReconnectReachesLocalLostAndTerminatesTheWatchWithoutReplacement(
        PersistentWatchMode mode
    )
    {
        var proxy = new WatchReconnectProxy(
            _fixture.ConnectionString,
            TestContext.Current.CancellationToken
        );
        await using var proxyLifetime = proxy.ConfigureAwait(true);
        var owner = await _fixture
            .ConnectAsync(connectionString: proxy.Endpoint, sessionTimeout: ShortSessionTimeout)
            .ConfigureAwait(true);
        await using var ownerLifetime = owner.ConfigureAwait(true);
        Assert.True(owner.Session.NegotiatedTimeout <= EventGuard);
        var watch = await owner
            .AddWatchAsync(_fixture.Root, mode, TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        var states = owner.SubscribeStateChanges();
        await using var statesLifetime = states.ConfigureAwait(true);
        var stateReader = states
            .ReadStatesAsync(TestContext.Current.CancellationToken)
            .GetAsyncEnumerator(TestContext.Current.CancellationToken);
        await using var stateLifetime = stateReader.ConfigureAwait(true);
        Assert.True(await stateReader.MoveNextAsync().ConfigureAwait(true));
        long id = owner.Session.SessionId;
        long generation = owner.Session.ConnectionGeneration;
        var events = watch
            .ReadEventsAsync(TestContext.Current.CancellationToken)
            .GetAsyncEnumerator(TestContext.Current.CancellationToken);
        await using var eventLifetime = events.ConfigureAwait(true);
        proxy.DropAndPause();
        await WaitStateAsync(stateReader, ZooKeeperSessionState.Disconnected).ConfigureAwait(true);
        var readiness = owner.WaitForConnectionAsync(TestContext.Current.CancellationToken);
        var error = await Assert
            .ThrowsAsync<ZooKeeperSessionException>(() =>
                events
                    .MoveNextAsync()
                    .AsTask()
                    .WaitAsync(EventGuard, TestContext.Current.CancellationToken)
            )
            .ConfigureAwait(true);
        Assert.Equal(ZooKeeperSessionState.Lost, error.State);
        Assert.Null(error.ErrorCode);
        Assert.Equal(ZooKeeperSessionState.Lost, owner.Session.State);
        await Assert.ThrowsAsync<ZooKeeperSessionException>(() => readiness).ConfigureAwait(true);
        proxy.Resume();
        await Assert
            .ThrowsAsync<ZooKeeperSessionException>(() =>
                owner.WaitForConnectionAsync(TestContext.Current.CancellationToken)
            )
            .ConfigureAwait(true);
        Assert.Equal(id, owner.Session.SessionId);
        Assert.Equal(generation, owner.Session.ConnectionGeneration);
        Assert.Equal(0, proxy.RestoreReplies);
        var rejected = await Assert
            .ThrowsAsync<ZooKeeperException>(() =>
                owner.SyncAsync(_fixture.Root, TestContext.Current.CancellationToken)
            )
            .ConfigureAwait(true);
        Assert.Equal(RequestOutcome.NotSent, rejected.Outcome);
    }

    private static async Task WaitStateAsync(
        IAsyncEnumerator<ZooKeeperSessionSnapshot> reader,
        ZooKeeperSessionState state
    )
    {
        do
        {
            Assert.True(
                await reader
                    .MoveNextAsync()
                    .AsTask()
                    .WaitAsync(EventGuard, TestContext.Current.CancellationToken)
                    .ConfigureAwait(false)
            );
        } while (reader.Current.State != state);
    }
}
