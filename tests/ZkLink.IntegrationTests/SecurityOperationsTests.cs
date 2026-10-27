namespace ZkLink.IntegrationTests;

[Collection("Security")]
[Trait("Fixture", "Security")]
public sealed class SecurityOperationsTests : IAsyncLifetime
{
    private const int TestTimeoutMilliseconds = 60_000;
    private static readonly TimeSpan EventGuard = TimeSpan.FromSeconds(15);
    private readonly SecurityFixture _fixture = new();
    private ZooKeeperClient Client => _fixture.Client;

    public ValueTask InitializeAsync() => _fixture.InitializeAsync();

    public ValueTask DisposeAsync() => _fixture.DisposeAsync();

    [Theory(Timeout = TestTimeoutMilliseconds)]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [Trait("Acceptance", "T20")]
    public async Task EverySecureNodeAcceptsTrustedDigestOperationsAndAcknowledgedClose(int node)
    {
        var client = await _fixture
            .ConnectAsync(_fixture.ConnectionString.Split(',')[node])
            .ConfigureAwait(true);
        await using var lifetime = client.ConfigureAwait(true);
        string path = _fixture.Root + "/node";
        await client
            .CreateAsync(
                path,
                new byte[] { 1 },
                _fixture.DigestCreate,
                TestContext.Current.CancellationToken
            )
            .ConfigureAwait(true);
        var data = await client
            .GetDataAsync(path, TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        Assert.Equal((byte)1, Assert.Single(data.Data!.Value.ToArray()));
        await client
            .DeleteAsync(path, ZNodeVersion.Any, TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        await client.CloseAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
        Assert.Equal(ZooKeeperCloseStatus.Acknowledged, client.Session.CloseStatus);
        Assert.True(_fixture.Certificate.HasPrivateKey);
    }

    [Fact(Timeout = TestTimeoutMilliseconds)]
    [Trait("Acceptance", "T19")]
    [Trait("Acceptance", "T21")]
    public async Task SecureCrudTransactionsAndDigestAclRemainIndependentOfTlsAuthentication()
    {
        string path = _fixture.Root + "/data";
        await Client
            .CreateAsync(path, null, _fixture.DigestCreate, TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        await Client
            .SetDataAsync(
                path,
                new byte[] { 2 },
                ZNodeVersion.Exact(0),
                TestContext.Current.CancellationToken
            )
            .ConfigureAwait(true);
        Assert.Equal(
            1,
            (
                await Client
                    .ExistsAsync(path, TestContext.Current.CancellationToken)
                    .ConfigureAwait(true)
            )!
                .Value
                .Version
        );
        Assert.Contains(
            "data",
            (
                await Client
                    .GetChildrenAsync(_fixture.Root, TestContext.Current.CancellationToken)
                    .ConfigureAwait(true)
            ).Children
        );
        await Client
            .MultiAsync(
                [
                    ZooKeeperWriteOperation.Create(
                        _fixture.Root + "/batch",
                        null,
                        _fixture.DigestCreate
                    ),
                    ZooKeeperWriteOperation.SetData(
                        _fixture.Root + "/batch",
                        new byte[] { 3 },
                        ZNodeVersion.Exact(0)
                    ),
                    ZooKeeperWriteOperation.Check(_fixture.Root + "/batch", ZNodeVersion.Exact(1)),
                ],
                TestContext.Current.CancellationToken
            )
            .ConfigureAwait(true);
        var reads = await Client
            .MultiReadAsync(
                [
                    ZooKeeperReadOperation.GetData(path),
                    ZooKeeperReadOperation.GetChildren(_fixture.Root),
                ],
                TestContext.Current.CancellationToken
            )
            .ConfigureAwait(true);
        Assert.Equal(2, reads.Count);
        var readData = Assert.IsType<ZooKeeperDataReadResult>(reads[0]);
        Assert.Equal((byte)2, Assert.Single(readData.Value.Data!.Value.ToArray()));
        Assert.Contains("batch", Assert.IsType<ZooKeeperChildrenReadResult>(reads[1]).Value);
        var committed = await Client
            .GetDataAsync(_fixture.Root + "/batch", TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        Assert.Equal(1, committed.Stat.Version);
        Assert.Equal((byte)3, Assert.Single(committed.Data!.Value.ToArray()));
        var identities = await Client
            .WhoAmIAsync(TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        Assert.Contains(identities, static identity => identity.Scheme == "digest");
        Assert.Equal(_fixture.Mutual, identities.Any(static identity => identity.Scheme == "x509"));
        var certificateOnly = await _fixture.ConnectAsync(digest: false).ConfigureAwait(true);
        await using var otherLifetime = certificateOnly.ConfigureAwait(true);
        var error = await Assert
            .ThrowsAsync<ZooKeeperException>(() =>
                certificateOnly.GetDataAsync(path, TestContext.Current.CancellationToken)
            )
            .ConfigureAwait(true);
        Assert.Equal(ZooKeeperErrorCode.NoAuth, error.KnownErrorCode);
        Assert.Equal(RequestOutcome.Rejected, error.Outcome);
        _fixture.AssertSafe(error);
        await certificateOnly
            .SyncAsync(_fixture.Root, TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        Assert.Equal(ZooKeeperSessionState.Connected, certificateOnly.Session.State);
        await Client
            .SetAclAsync(
                path,
                _fixture.DigestCreate.Acl,
                ZNodeVersion.Any,
                TestContext.Current.CancellationToken
            )
            .ConfigureAwait(true);
        await Client
            .DeleteAsync(path, ZNodeVersion.Any, TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
    }

    [Fact(Timeout = TestTimeoutMilliseconds)]
    [Trait("Acceptance", "T20")]
    [Trait("Acceptance", "T26")]
    public async Task SecureReconnectReplaysDigestBeforeRestoringOneShotAndPersistentWatches()
    {
        string path = _fixture.Root + "/persistent";
        string once = _fixture.Root + "/once";
        await Client
            .CreateAsync(path, null, _fixture.DigestCreate, TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        await Client
            .CreateAsync(once, null, _fixture.DigestCreate, TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        // Opaque TLS records cross this tunnel; it never terminates TLS or inspects Jute.
        var proxy = new WatchReconnectProxy(
            _fixture.ConnectionString,
            inspectFrames: false,
            TestContext.Current.CancellationToken
        );
        await using var proxyLifetime = proxy.ConfigureAwait(true);
        var owner = await _fixture.ConnectAsync(proxy.Endpoint).ConfigureAwait(true);
        await using var ownerLifetime = owner.ConfigureAwait(true);
        var persistent = await owner
            .AddWatchAsync(
                path,
                PersistentWatchMode.Persistent,
                TestContext.Current.CancellationToken
            )
            .ConfigureAwait(true);
        var oneShot = await owner
            .GetDataAndWatchAsync(once, TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        var states = owner.SubscribeStateChanges();
        await using var statesLifetime = states.ConfigureAwait(true);
        var stateReader = states
            .ReadStatesAsync(TestContext.Current.CancellationToken)
            .GetAsyncEnumerator(TestContext.Current.CancellationToken);
        await using var stateLifetime = stateReader.ConfigureAwait(true);
        Assert.True(await stateReader.MoveNextAsync().ConfigureAwait(true));
        long session = owner.Session.SessionId;
        long generation = owner.Session.ConnectionGeneration;
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
        await Client
            .SetDataAsync(
                once,
                new byte[] { 1 },
                ZNodeVersion.Any,
                TestContext.Current.CancellationToken
            )
            .ConfigureAwait(true);
        proxy.Resume();
        await owner
            .WaitForConnectionAsync(TestContext.Current.CancellationToken)
            .WaitAsync(EventGuard, TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        Assert.Equal(session, owner.Session.SessionId);
        Assert.True(owner.Session.ConnectionGeneration > generation);
        var events = persistent
            .ReadEventsAsync(TestContext.Current.CancellationToken)
            .GetAsyncEnumerator(TestContext.Current.CancellationToken);
        await using var eventLifetime = events.ConfigureAwait(true);
        Assert.True(
            await events
                .MoveNextAsync()
                .AsTask()
                .WaitAsync(EventGuard, TestContext.Current.CancellationToken)
                .ConfigureAwait(true)
        );
        Assert.Equal(ZooKeeperWatchEventKind.ReconnectedWithGap, events.Current.Kind);
        var onceEvents = oneShot
            .Watch.ReadEventsAsync(TestContext.Current.CancellationToken)
            .GetAsyncEnumerator(TestContext.Current.CancellationToken);
        await using var onceLifetime = onceEvents.ConfigureAwait(true);
        Assert.True(
            await onceEvents
                .MoveNextAsync()
                .AsTask()
                .WaitAsync(EventGuard, TestContext.Current.CancellationToken)
                .ConfigureAwait(true)
        );
        Assert.Equal(ZooKeeperWatchEventKind.NodeDataChanged, onceEvents.Current.Kind);
        Assert.False(await onceEvents.MoveNextAsync().ConfigureAwait(true));
        await Client
            .SetDataAsync(
                path,
                new byte[] { 2 },
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
        await owner.GetDataAsync(path, TestContext.Current.CancellationToken).ConfigureAwait(true);
        await owner.CloseAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
        Assert.Equal(ZooKeeperCloseStatus.Acknowledged, owner.Session.CloseStatus);
    }
}
