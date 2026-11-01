using ZkLink.IntegrationTests;

namespace ZkLink.FaultTests;

[Trait("Fixture", "SessionExpiry")]
public sealed class SessionExpiryTests : IAsyncLifetime
{
    private const int TestTimeoutMilliseconds = 90_000;
    private const int ProbeRounds = 3;

    // Owned voters use server IDs 1..3; none can issue the all-ones session ID.
    private const long UnknownSessionId = -1;
    private const string AttemptPrefix = "attempt-";
    private static readonly TimeSpan EventGuard = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan ProbeInterval = TimeSpan.FromSeconds(1);
    private readonly LiveZooKeeperFixture _fixture = new();

    public ValueTask InitializeAsync()
    {
        var fault = new MemberControl();
        if (fault.Role != MemberControl.Expiry)
        {
            throw new InvalidOperationException("Run the owned server-expiry fixture.");
        }
        return _fixture.InitializeAsync();
    }

    public ValueTask DisposeAsync() => _fixture.DisposeAsync();

    [Fact(Timeout = TestTimeoutMilliseconds)]
    [Trait("Acceptance", "T10")]
    [Trait("Acceptance", "T14")]
    [Trait("Acceptance", "T26")]
    [Trait("Acceptance", "T32")]
    public async Task ServerExpiryTerminatesSessionAndWatchesWithoutReplacementOrCreateReplay()
    {
        var token = TestContext.Current.CancellationToken;
        var fault = new MemberControl();
        var proxy = new FaultProxy(fault.Endpoint, holdRestore: false, token);
        await using var proxyLifetime = proxy.ConfigureAwait(true);
        var owner = await _fixture
            .ConnectAsync(connectionString: proxy.Endpoint)
            .ConfigureAwait(true);
        await using var ownerLifetime = owner.ConfigureAwait(true);
        string path = _fixture.Root + "/watched";
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
        await FenceAsync(token).ConfigureAwait(true);
        var before = await _fixture.Client.ExistsAsync(lease, token).ConfigureAwait(true);
        Assert.NotNull(before);
        Assert.Equal(owner.Session.SessionId, before.Value.EphemeralOwner);
        long session = owner.Session.SessionId;
        await Assert
            .ThrowsAsync<InvalidOperationException>(() =>
                fault.ExpireSessionAsync(UnknownSessionId, token)
            )
            .ConfigureAwait(true);
        await owner.SyncAsync(path, token).ConfigureAwait(true);
        Assert.Equal(ZooKeeperSessionState.Connected, owner.Session.State);
        TestContext.Current.TestOutputHelper?.WriteLine(
            "Unknown session control: rejected; valid owner remains usable."
        );

        var once = await owner.GetDataAndWatchAsync(path, token).ConfigureAwait(true);
        await using var onceLifetime = once.Watch.ConfigureAwait(true);
        var persistent = await owner
            .AddWatchAsync(
                _fixture.Root + "/recursive",
                PersistentWatchMode.PersistentRecursive,
                token
            )
            .ConfigureAwait(true);
        await using var persistentLifetime = persistent.ConfigureAwait(true);
        var onceEvents = new EventProbe<ZooKeeperWatchEvent>(
            once.Watch.ReadEventsAsync(token),
            token
        );
        await using var onceEventsLifetime = onceEvents.ConfigureAwait(true);
        var persistentEvents = new EventProbe<ZooKeeperWatchEvent>(
            persistent.ReadEventsAsync(token),
            token
        );
        await using var persistentEventsLifetime = persistentEvents.ConfigureAwait(true);
        var onceNext = onceEvents.MoveNextAsync();
        var persistentNext = persistentEvents.MoveNextAsync();
        var states = owner.SubscribeStateChanges();
        await using var statesLifetime = states.ConfigureAwait(true);
        var stateEvents = new EventProbe<ZooKeeperSessionSnapshot>(
            states.ReadStatesAsync(token),
            token
        );
        await using var stateLifetime = stateEvents.ConfigureAwait(true);
        Assert.True(await stateEvents.MoveNextAsync().ConfigureAwait(true));

        int createsBefore = proxy.CreateTransmissions;
        proxy.ArmCreateReply();
        var create = owner.CreateAsync(
            _fixture.Root + "/" + AttemptPrefix,
            null,
            new CreateOptions(
                LiveZooKeeperFixture.OwnerCreate.Acl,
                CreateMode.PersistentSequential
            ),
            token
        );
        try
        {
            await proxy.ResponseWithheld.WaitAsync(EventGuard, token).ConfigureAwait(true);
            Assert.Equal(FaultProxy.Success, proxy.WithheldError);
            Assert.False(create.IsCompleted);
            Assert.Equal(createsBefore + 1, proxy.CreateTransmissions);

            await fault.ExpireSessionAsync(session, token).ConfigureAwait(true);
            do
            {
                Assert.True(
                    await stateEvents
                        .MoveNextAsync()
                        .WaitAsync(EventGuard, token)
                        .ConfigureAwait(true)
                );
                Assert.NotEqual(ZooKeeperSessionState.Lost, stateEvents.Current.State);
            } while (stateEvents.Current.State != ZooKeeperSessionState.Expired);

            var uncertain = await Assert
                .ThrowsAsync<ZooKeeperException>(() => create)
                .ConfigureAwait(true);
            Assert.Equal(RequestOutcome.Unknown, uncertain.Outcome);
            Assert.IsNotType<ZooKeeperTimeoutException>(uncertain);
            Assert.Equal(ZooKeeperOperation.CreateWithStat, uncertain.Request.Operation);
            await AssertExpiredAsync(onceNext).ConfigureAwait(true);
            await AssertExpiredAsync(persistentNext).ConfigureAwait(true);
            await AssertExpiredAsync(owner.WaitForConnectionAsync(token)).ConfigureAwait(true);
            await FenceAsync(token).ConfigureAwait(true);
            Assert.Null(await _fixture.Client.ExistsAsync(lease, token).ConfigureAwait(true));
            long generation = owner.Session.ConnectionGeneration;
            int attempts = proxy.ConnectionAttempts;
            for (int round = 0; round < ProbeRounds; round++)
            {
                await _fixture
                    .Client.SetDataAsync(path, null, ZNodeVersion.Any, token)
                    .ConfigureAwait(true);
                var children = await _fixture
                    .Client.GetChildrenAsync(_fixture.Root, token)
                    .ConfigureAwait(true);
                Assert.Single(
                    children.Children,
                    child => child.StartsWith(AttemptPrefix, StringComparison.Ordinal)
                );
                Assert.Null(await _fixture.Client.ExistsAsync(lease, token).ConfigureAwait(true));
                Assert.Equal(ZooKeeperSessionState.Expired, owner.Session.State);
                Assert.Equal(session, owner.Session.SessionId);
                Assert.Equal(generation, owner.Session.ConnectionGeneration);
                Assert.Equal(attempts, proxy.ConnectionAttempts);
                Assert.Equal(createsBefore + 1, proxy.CreateTransmissions);
                var rejected = await Assert
                    .ThrowsAsync<ZooKeeperException>(() => owner.SyncAsync(path, token))
                    .ConfigureAwait(true);
                Assert.Equal(RequestOutcome.NotSent, rejected.Outcome);
                await Task.Delay(ProbeInterval, token).ConfigureAwait(true);
            }
            await owner.CloseAsync(token).ConfigureAwait(true);
            Assert.Equal(ZooKeeperCloseStatus.SessionExpired, owner.Session.CloseStatus);
            Assert.Null(proxy.RunFailure);
            TestContext.Current.TestOutputHelper?.WriteLine(
                "Server expiry: PASS (Expired, terminal watches/readiness, ephemeral removed, Unknown, one committed create, no replacement/replay)."
            );
        }
        finally
        {
            await owner.DisposeAsync().ConfigureAwait(true);
            try
            {
                await create.ConfigureAwait(true);
            }
            catch (Exception error) when (error is ZooKeeperException or OperationCanceledException)
            { }
        }
    }

    private Task<ZNodeStat> FenceAsync(CancellationToken token) =>
        _fixture.Client.SetDataAsync(_fixture.Root, null, ZNodeVersion.Any, token);

    private static async Task AssertExpiredAsync(Task operation)
    {
        var error = await Assert
            .ThrowsAsync<ZooKeeperSessionException>(() =>
                operation.WaitAsync(EventGuard, TestContext.Current.CancellationToken)
            )
            .ConfigureAwait(true);
        Assert.Equal(ZooKeeperSessionState.Expired, error.State);
    }
}
