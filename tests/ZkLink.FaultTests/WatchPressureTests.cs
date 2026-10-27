using System.Diagnostics;
using System.Globalization;
using ZkLink.IntegrationTests;
using ZkLink.Testing;

namespace ZkLink.FaultTests;

[CollectionDefinition("WatchPressure", DisableParallelization = true)]
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Maintainability",
    "CA1515",
    Justification = "xUnit collection definitions must be public for discovery (xUnit1027)."
)]
public sealed class WatchPressureIsolation;

[Collection("WatchPressure")]
[Trait("Fixture", "WatchPressure")]
public sealed class WatchPressureTests : IAsyncLifetime
{
    private const int TestTimeoutMilliseconds = 60_000;
    private const int HandleCount = 3;
    private const int RoundCount = 3;
    private const int UnreadDisposalRound = 1;
    private const int SlowCapacity = 12;
    private const int RetainedStateCount = 1;
    private const int BurstEventCount = 3;
    private const int OverflowCapacity = RetainedStateCount + HandleCount * BurstEventCount;
    private const int RequiredHeartbeatCount = 2;
    private const string HeartbeatMetric = "zklink.heartbeat.roundtrip";
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan HeartbeatGuard = TimeSpan.FromSeconds(12);
    private static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(20);
    private static readonly TimeSpan SessionTimeout = TimeSpan.FromSeconds(6);
    private static readonly TimeSpan ConnectGuard = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan AttemptGuard = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan CloseGuard = TimeSpan.FromSeconds(2);
    private static readonly ZooKeeperWatchEventKind[] ExpectedKinds =
    [
        ZooKeeperWatchEventKind.NodeDataChanged,
        ZooKeeperWatchEventKind.NodeDeleted,
        ZooKeeperWatchEventKind.NodeCreated,
    ];
    private static readonly string[] OwnedCounters =
    [
        "zklink.watches.active",
        "zklink.events.backlog",
        "zklink.requests.outstanding",
        "zklink.requests.bytes",
        "zklink.requests.tombstones",
    ];
    private static string Endpoint =>
        Environment.GetEnvironmentVariable("ZKLINK_FAULT_ENDPOINT")
        ?? throw new InvalidOperationException(
            "Run just zk-watch-pressure-faults with its owned fixture."
        );
    private readonly LiveZooKeeperFixture _fixture = new();

    public ValueTask InitializeAsync()
    {
        if (Environment.GetEnvironmentVariable("ZKLINK_FAULT_ROLE") != "pressure")
        {
            throw new InvalidOperationException(
                "Run just zk-watch-pressure-faults with its owned fixture."
            );
        }
        _ = Endpoint;
        return _fixture.InitializeAsync();
    }

    public ValueTask DisposeAsync() => _fixture.DisposeAsync();

    [Fact(Timeout = TestTimeoutMilliseconds)]
    [Trait("Acceptance", "T25")]
    [Trait("Acceptance", "T27")]
    [Trait("Acceptance", "T33")]
    [Trait("Acceptance", "T34")]
    public async Task UnreadFanoutAllowsHeartbeatsAndRepeatedDrainRemovalAndDisposal()
    {
        var token = TestContext.Current.CancellationToken;
        using var capture = new TelemetryCapture();
        await CountersAsync(capture, token).ConfigureAwait(true);
        var proxy = new FaultProxy(Endpoint, holdRestore: false, token);
        await using var proxyLifetime = proxy.ConfigureAwait(true);
        var owner = await ConnectAsync(proxy.Endpoint, SlowCapacity, token).ConfigureAwait(true);
        await using var ownerLifetime = owner.ConfigureAwait(true);
        long session = owner.Session.SessionId;
        long generation = owner.Session.ConnectionGeneration;
        Assert.True(owner.Session.NegotiatedTimeout <= SessionTimeout);
        for (int round = 0; round < RoundCount; round++)
        {
            string parent = _fixture.Root + "/slow-" + round.ToString(CultureInfo.InvariantCulture);
            string path = await CreatePathsAsync(parent, token).ConfigureAwait(true);
            var watches = new List<ZooKeeperWatch>();
            try
            {
                await RegisterAsync(owner, parent, path, watches, token).ConfigureAwait(true);
                await CountersAsync(capture, token, watches: HandleCount).ConfigureAwait(true);
                var rejected = await Assert
                    .ThrowsAsync<ZooKeeperBackpressureException>(() =>
                        owner.AddWatchAsync(path, PersistentWatchMode.Persistent, token)
                    )
                    .ConfigureAwait(true);
                Assert.Equal(RequestOutcome.NotSent, rejected.Outcome);
                await BurstAsync(owner, path, capture, retainedStates: 0, token)
                    .ConfigureAwait(true);
                Step("slow-queues-filled-round-" + round);
                Step("pressure-counts " + Snapshot(capture));
                if (round == 0)
                {
                    int pings = proxy.ForwardedPings;
                    int replies = HeartbeatReplies(capture);
                    await WaitAsync(
                            () =>
                                proxy.ForwardedPings >= pings + RequiredHeartbeatCount
                                && HeartbeatReplies(capture) >= replies + RequiredHeartbeatCount,
                            "real heartbeat progress with unread queues",
                            token,
                            HeartbeatGuard
                        )
                        .ConfigureAwait(true);
                    Assert.True(proxy.HasOpenConnection);
                    await CountersAsync(capture, token, HandleCount, HandleCount * BurstEventCount)
                        .ConfigureAwait(true);
                    Step("heartbeats-forwarded-with-unread-events");
                    Step(
                        "heartbeat-counts forwarded="
                            + proxy.ForwardedPings
                            + " replies="
                            + HeartbeatReplies(capture)
                    );
                }
                var read = await owner.GetDataAsync(path, token).ConfigureAwait(true);
                Assert.Equal((byte)2, Assert.Single(read.Data!.Value.ToArray()));
                Assert.Equal(ZooKeeperSessionState.Connected, owner.Session.State);
                Assert.Equal(session, owner.Session.SessionId);
                Assert.Equal(generation, owner.Session.ConnectionGeneration);
                Assert.Equal(1, proxy.ConnectionAttempts);
                int firstToDrain = 0;
                if (round == UnreadDisposalRound)
                {
                    await watches[0].DisposeAsync().ConfigureAwait(true);
                    await CountersAsync(
                            capture,
                            token,
                            HandleCount - 1,
                            (HandleCount - 1) * BurstEventCount
                        )
                        .ConfigureAwait(true);
                    var disposedReader = new EventProbe<ZooKeeperWatchEvent>(
                        watches[0].ReadEventsAsync(token),
                        token
                    );
                    await using var disposedReaderLifetime = disposedReader.ConfigureAwait(true);
                    Assert.False(await disposedReader.MoveNextAsync().ConfigureAwait(true));
                    firstToDrain = 1;
                    Step("unread-sharer-disposed-budget-released");
                    Step("remaining-counts " + Snapshot(capture));
                }
                foreach (var watch in watches.Skip(firstToDrain))
                {
                    var reader = new EventProbe<ZooKeeperWatchEvent>(
                        watch.ReadEventsAsync(token),
                        token
                    );
                    await using var readerLifetime = reader.ConfigureAwait(true);
                    await DrainAsync(reader, watch, path, generation).ConfigureAwait(true);
                    var removed = await watch
                        .RemoveAsync(WatchRemovalMode.ServerConfirmed, token)
                        .ConfigureAwait(true);
                    Assert.True(
                        removed.Status
                            is WatchRemovalStatus.ServerConfirmed
                                or WatchRemovalStatus.SharedRegistrationRetained
                    );
                    Assert.False(await reader.MoveNextAsync().ConfigureAwait(true));
                }
                await CountersAsync(capture, token).ConfigureAwait(true);
                Step("drained-and-capacity-released-round-" + round);
                Step("released-counts " + Snapshot(capture));
            }
            finally
            {
                foreach (var watch in watches)
                {
                    await watch.DisposeAsync().ConfigureAwait(true);
                }
            }
        }
        await owner.CloseAsync(token).ConfigureAwait(true);
        Assert.Equal(ZooKeeperCloseStatus.Acknowledged, owner.Session.CloseStatus);
        await owner.DisposeAsync().ConfigureAwait(true);
        await CountersAsync(capture, token).ConfigureAwait(true);
        Assert.Null(proxy.RunFailure);
        capture.AssertRedacted(_fixture.Root);
        Step("slow-owner-disposed-counters-at-baseline");
    }

    [Fact(Timeout = TestTimeoutMilliseconds)]
    [Trait("Acceptance", "T30")]
    [Trait("Acceptance", "T33")]
    [Trait("Acceptance", "T34")]
    public async Task StateAndWatchBudgetOverflowRetainsBufferedOrderAndFaultsExplicitly()
    {
        var token = TestContext.Current.CancellationToken;
        using var capture = new TelemetryCapture();
        await CountersAsync(capture, token).ConfigureAwait(true);
        capture.Collect();
        double overflows = capture.Value("zklink.event.overflows");
        var proxy = new FaultProxy(Endpoint, holdRestore: false, token);
        await using var proxyLifetime = proxy.ConfigureAwait(true);
        var owner = await ConnectAsync(proxy.Endpoint, OverflowCapacity, token)
            .ConfigureAwait(true);
        await using var ownerLifetime = owner.ConfigureAwait(true);
        long session = owner.Session.SessionId;
        long generation = owner.Session.ConnectionGeneration;
        var states = owner.SubscribeStateChanges();
        await using var statesLifetime = states.ConfigureAwait(true);
        string parent = _fixture.Root + "/overflow";
        string path = await CreatePathsAsync(parent, token).ConfigureAwait(true);
        var watches = new List<ZooKeeperWatch>();
        try
        {
            await RegisterAsync(owner, parent, path, watches, token).ConfigureAwait(true);
            await BurstAsync(owner, path, capture, RetainedStateCount, token).ConfigureAwait(true);
            await CountersAsync(capture, token, HandleCount, OverflowCapacity).ConfigureAwait(true);
            Assert.Equal(ZooKeeperSessionState.Connected, owner.Session.State);
            var fullRead = await owner.GetDataAsync(path, token).ConfigureAwait(true);
            Assert.Equal((byte)2, Assert.Single(fullRead.Data!.Value.ToArray()));
            await CountersAsync(capture, token, HandleCount, OverflowCapacity).ConfigureAwait(true);
            Step("shared-state-watch-budget-full");
            Step("full-counts " + Snapshot(capture));
            await _fixture
                .Client.SetDataAsync(path, new byte[] { 3 }, ZNodeVersion.Any, token)
                .ConfigureAwait(true);
            await WaitAsync(
                    () => owner.Session.State == ZooKeeperSessionState.Faulted,
                    "explicit overflow fault",
                    token
                )
                .ConfigureAwait(true);
            await Assert
                .ThrowsAsync<ZooKeeperEventOverflowException>(() =>
                    owner.WaitForConnectionAsync(token)
                )
                .ConfigureAwait(true);
            var denied = await Assert
                .ThrowsAsync<ZooKeeperException>(() => owner.GetDataAsync(path, token))
                .ConfigureAwait(true);
            Assert.Equal(RequestOutcome.NotSent, denied.Outcome);
            await CountersAsync(capture, token, backlog: OverflowCapacity).ConfigureAwait(true);
            foreach (var watch in watches)
            {
                var reader = new EventProbe<ZooKeeperWatchEvent>(
                    watch.ReadEventsAsync(token),
                    token
                );
                await using var readerLifetime = reader.ConfigureAwait(true);
                await DrainAsync(reader, watch, path, generation).ConfigureAwait(true);
                await Assert
                    .ThrowsAsync<ZooKeeperEventOverflowException>(() => reader.MoveNextAsync())
                    .ConfigureAwait(true);
            }
            var stateReader = new EventProbe<ZooKeeperSessionSnapshot>(
                states.ReadStatesAsync(token),
                token
            );
            await using var stateReaderLifetime = stateReader.ConfigureAwait(true);
            Assert.True(await stateReader.MoveNextAsync().ConfigureAwait(true));
            Assert.Equal(ZooKeeperSessionState.Connected, stateReader.Current.State);
            await Assert
                .ThrowsAsync<ZooKeeperEventOverflowException>(() => stateReader.MoveNextAsync())
                .ConfigureAwait(true);
            await _fixture
                .Client.SetDataAsync(path, new byte[] { 4 }, ZNodeVersion.Any, token)
                .ConfigureAwait(true);
            await _fixture.Client.SyncAsync(parent, token).ConfigureAwait(true);
            Assert.Equal(
                (byte)4,
                Assert.Single(
                    (
                        await _fixture.Client.GetDataAsync(path, token).ConfigureAwait(true)
                    ).Data!.Value.ToArray()
                )
            );
            Assert.Equal(session, owner.Session.SessionId);
            Assert.Equal(generation, owner.Session.ConnectionGeneration);
            Assert.Equal(ZooKeeperSessionState.Faulted, owner.Session.State);
            Assert.Equal(1, proxy.ConnectionAttempts);
            await CountersAsync(capture, token).ConfigureAwait(true);
            capture.Collect();
            Assert.Equal(overflows + 1, capture.Value("zklink.event.overflows"));
            Step("overflow-explicit-buffered-fifo-retained-no-replacement");
            Step(
                "fault-counts "
                    + Snapshot(capture)
                    + " overflows="
                    + (capture.Value("zklink.event.overflows") - overflows)
            );
        }
        finally
        {
            foreach (var watch in watches)
            {
                await watch.DisposeAsync().ConfigureAwait(true);
            }
        }
        await owner.DisposeAsync().ConfigureAwait(true);
        await CountersAsync(capture, token).ConfigureAwait(true);
        capture.AssertRedacted(_fixture.Root);
        Step("overflow-owner-disposed-counters-at-baseline");
    }

    private async Task<ZooKeeperClient> ConnectAsync(
        string endpoint,
        int capacity,
        CancellationToken token
    )
    {
        using var credentials = _fixture.CreateAuthentication();
        return await ZooKeeperClient
            .ConnectAsync(
                new ZooKeeperClientOptions
                {
                    ConnectionString = endpoint,
                    CompatibilityProfile = _fixture.CompatibilityProfile,
                    Tls = ZooKeeperTlsOptions.Disabled(),
                    AllowUnsafePlaintextDigest = true,
                    Authentication = [credentials],
                    MaxBufferedEvents = capacity,
                    MaxWatches = HandleCount,
                    SessionTimeout = SessionTimeout,
                    InitialConnectTimeout = ConnectGuard,
                    ConnectAttemptTimeout = AttemptGuard,
                    OperationTimeout = ConnectGuard,
                    CloseTimeout = CloseGuard,
                },
                token
            )
            .ConfigureAwait(true);
    }

    private async Task<string> CreatePathsAsync(string parent, CancellationToken token)
    {
        await _fixture
            .Client.CreateAsync(parent, null, LiveZooKeeperFixture.OwnerCreate, token)
            .ConfigureAwait(true);
        string path = parent + "/node";
        await _fixture
            .Client.CreateAsync(path, null, LiveZooKeeperFixture.OwnerCreate, token)
            .ConfigureAwait(true);
        return path;
    }

    private static async Task RegisterAsync(
        ZooKeeperClient owner,
        string parent,
        string path,
        List<ZooKeeperWatch> watches,
        CancellationToken token
    )
    {
        watches.Add(
            await owner
                .AddWatchAsync(path, PersistentWatchMode.Persistent, token)
                .ConfigureAwait(true)
        );
        watches.Add(
            await owner
                .AddWatchAsync(path, PersistentWatchMode.Persistent, token)
                .ConfigureAwait(true)
        );
        watches.Add(
            await owner
                .AddWatchAsync(parent, PersistentWatchMode.PersistentRecursive, token)
                .ConfigureAwait(true)
        );
    }

    private async Task BurstAsync(
        ZooKeeperClient owner,
        string path,
        TelemetryCapture capture,
        int retainedStates,
        CancellationToken token
    )
    {
        for (int index = 0; index < BurstEventCount; index++)
        {
            switch (ExpectedKinds[index])
            {
                case ZooKeeperWatchEventKind.NodeDataChanged:
                    await _fixture
                        .Client.SetDataAsync(path, new byte[] { 1 }, ZNodeVersion.Any, token)
                        .ConfigureAwait(true);
                    break;
                case ZooKeeperWatchEventKind.NodeDeleted:
                    await _fixture
                        .Client.DeleteAsync(path, ZNodeVersion.Any, token)
                        .ConfigureAwait(true);
                    break;
                case ZooKeeperWatchEventKind.NodeCreated:
                    await _fixture
                        .Client.CreateAsync(
                            path,
                            new byte[] { 2 },
                            LiveZooKeeperFixture.OwnerCreate,
                            token
                        )
                        .ConfigureAwait(true);
                    break;
                default:
                    throw new InvalidOperationException(
                        "The pressure burst must have a defined mutation."
                    );
            }
            await owner.SyncAsync(_fixture.Root, token).ConfigureAwait(true);
            await CountersAsync(
                    capture,
                    token,
                    HandleCount,
                    retainedStates + HandleCount * (index + 1)
                )
                .ConfigureAwait(true);
        }
    }

    private static async Task DrainAsync(
        EventProbe<ZooKeeperWatchEvent> reader,
        ZooKeeperWatch watch,
        string path,
        long generation
    )
    {
        foreach (var kind in ExpectedKinds)
        {
            Assert.True(await reader.MoveNextAsync().ConfigureAwait(true));
            Assert.Equal(kind, reader.Current.Kind);
            Assert.Equal(path, reader.Current.Path);
            Assert.Equal(watch.RegistrationId, reader.Current.RegistrationId);
            Assert.Equal(generation, reader.Current.ConnectionGeneration);
        }
    }

    private static Task CountersAsync(
        TelemetryCapture capture,
        CancellationToken token,
        int watches = 0,
        int backlog = 0
    ) =>
        WaitAsync(
            () =>
            {
                capture.Collect();
                return OwnedCounters.All(capture.Values.ContainsKey)
                    && capture.Value("zklink.watches.active") == watches
                    && capture.Value("zklink.events.backlog") == backlog
                    && capture.Value("zklink.requests.outstanding") == 0
                    && capture.Value("zklink.requests.bytes") == 0
                    && capture.Value("zklink.requests.tombstones") == 0;
            },
            "owned counters at expected occupancy",
            token
        );

    private static async Task WaitAsync(
        Func<bool> condition,
        string description,
        CancellationToken token,
        TimeSpan? guard = null
    )
    {
        var clock = Stopwatch.StartNew();
        while (!condition())
        {
            if (clock.Elapsed >= (guard ?? Guard))
            {
                throw new TimeoutException(description);
            }
            await Task.Delay(Poll, token).ConfigureAwait(true);
        }
    }

    private static int HeartbeatReplies(TelemetryCapture capture) =>
        capture.Histograms.Count(static sample => sample.Name == HeartbeatMetric);

    private static string Snapshot(TelemetryCapture capture) =>
        string.Join(
            ",",
            OwnedCounters.Select(name =>
                name + "=" + capture.Value(name).ToString(CultureInfo.InvariantCulture)
            )
        );

    private static void Step(string value) =>
        TestContext.Current.TestOutputHelper?.WriteLine(value);
}
