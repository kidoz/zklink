using System.Diagnostics;
using ZkLink.IntegrationTests;
using ZkLink.Testing;

namespace ZkLink.FaultTests;

public sealed class MemberOutageTests : IAsyncLifetime
{
    private const int TestTimeoutMilliseconds = 90_000;
    private const string AttemptPrefix = "attempt-";
    private static readonly TimeSpan EventGuard = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan RecoveryAttempt = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan OperationGuard = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan RollbackGuard = TimeSpan.FromSeconds(50);
    private readonly LiveZooKeeperFixture _fixture = new();

    public ValueTask InitializeAsync()
    {
        _ = new MemberControl(); // Reject missing/foreign fixture metadata before opening any client.
        return _fixture.InitializeAsync();
    }

    public ValueTask DisposeAsync() => _fixture.DisposeAsync();

    [Fact(Timeout = TestTimeoutMilliseconds)]
    [Trait("Acceptance", "T32")]
    [Trait("Acceptance", "T14")]
    [Trait("Acceptance", "T26")]
    [Trait("Acceptance", "T34")]
    public async Task StoppedMemberPreservesSessionRestoresWatchesAndNeverReplaysUncertainCreate()
    {
        var clock = Stopwatch.StartNew();
        void Step(string name) =>
            TestContext.Current.TestOutputHelper?.WriteLine(
                $"{name} at {clock.ElapsedMilliseconds}ms"
            );
        var token = TestContext.Current.CancellationToken;
        var fault = new MemberControl();
        string[] survivors = _fixture
            .ConnectionString.Split(',')
            .Where(endpoint => endpoint != fault.Endpoint)
            .ToArray();
        Assert.Equal(2, survivors.Length);
        await _fixture
            .Client.UpdateEndpointsAsync(
                survivors
                    .Select(static endpoint => new ZooKeeperEndpoint("127.0.0.1", Port(endpoint)))
                    .ToArray(),
                token
            )
            .ConfigureAwait(true);
        using var capture = new TelemetryCapture();
        var source = new FaultProxy(fault.Endpoint, holdRestore: false, token);
        await using var sourceLifetime = source.ConfigureAwait(true);
        var first = new FaultProxy(survivors[0], holdRestore: true, token);
        await using var firstLifetime = first.ConfigureAwait(true);
        var second = new FaultProxy(survivors[1], holdRestore: true, token);
        await using var secondLifetime = second.ConfigureAwait(true);
        var control = await _fixture
            .ConnectAsync(connectionString: survivors[0])
            .ConfigureAwait(true);
        await using var controlLifetime = control.ConfigureAwait(true);
        var owner = await _fixture
            .ConnectAsync(
                connectionString: source.Endpoint,
                connectAttemptTimeout: RecoveryAttempt,
                operationTimeout: OperationGuard
            )
            .ConfigureAwait(true);
        await using var ownerLifetime = owner.ConfigureAwait(true);
        string oncePath = _fixture.Root + "/once";
        string persistentPath = _fixture.Root + "/persistent";
        string ephemeralPath = _fixture.Root + "/lease";
        foreach (string path in new[] { oncePath, persistentPath })
        {
            await control
                .CreateAsync(path, new byte[] { 1 }, LiveZooKeeperFixture.OwnerCreate, token)
                .ConfigureAwait(true);
        }
        await owner
            .CreateAsync(
                ephemeralPath,
                null,
                new CreateOptions(LiveZooKeeperFixture.OwnerCreate.Acl, CreateMode.Ephemeral),
                token
            )
            .ConfigureAwait(true);
        var oneShot = await owner.GetDataAndWatchAsync(oncePath, token).ConfigureAwait(true);
        await using var oneShotLifetime = oneShot.Watch.ConfigureAwait(true);
        var persistent = await owner
            .AddWatchAsync(persistentPath, PersistentWatchMode.Persistent, token)
            .ConfigureAwait(true);
        await using var persistentLifetime = persistent.ConfigureAwait(true);
        var events = new EventProbe<ZooKeeperWatchEvent>(persistent.ReadEventsAsync(token), token);
        await using var eventsLifetime = events.ConfigureAwait(true);
        var nextGap = events.MoveNextAsync();
        var onceEvents = new EventProbe<ZooKeeperWatchEvent>(
            oneShot.Watch.ReadEventsAsync(token),
            token
        );
        await using var onceEventsLifetime = onceEvents.ConfigureAwait(true);
        var nextOnce = onceEvents.MoveNextAsync();
        var states = owner.SubscribeStateChanges();
        await using var statesLifetime = states.ConfigureAwait(true);
        var stateReader = new EventProbe<ZooKeeperSessionSnapshot>(
            states.ReadStatesAsync(token),
            token
        );
        await using var stateReaderLifetime = stateReader.ConfigureAwait(true);
        Assert.True(await stateReader.MoveNextAsync().ConfigureAwait(true));
        long session = owner.Session.SessionId;
        long generation = owner.Session.ConnectionGeneration;
        int createsBefore = source.CreateTransmissions;
        Step("steady-state");
        await owner
            .UpdateEndpointsAsync(
                [
                    new ZooKeeperEndpoint("127.0.0.1", Port(first.Endpoint)),
                    new ZooKeeperEndpoint("127.0.0.1", Port(second.Endpoint)),
                ],
                token
            )
            .ConfigureAwait(true);
        Task<CreateResult>? uncertain = null;
        string? replacement = null;
        try
        {
            source.ArmCreateReply();
            uncertain = owner.CreateAsync(
                _fixture.Root + "/" + AttemptPrefix,
                new byte[] { 7 },
                new CreateOptions(
                    LiveZooKeeperFixture.OwnerCreate.Acl,
                    CreateMode.PersistentSequential
                ),
                token
            );
            await source.ResponseWithheld.WaitAsync(EventGuard, token).ConfigureAwait(true);
            Assert.Equal(FaultProxy.Success, source.WithheldError);
            Assert.False(source.ServerClosed.IsCompleted);
            Assert.False(uncertain.IsCompleted);
            await control.SyncAsync(_fixture.Root, token).ConfigureAwait(true);
            Assert.Single(
                (
                    await control.GetChildrenAsync(_fixture.Root, token).ConfigureAwait(true)
                ).Children,
                static child => child.StartsWith(AttemptPrefix, StringComparison.Ordinal)
            );
            Step("committed-reply-withheld");
            Step(fault.Role + "-stop-requested");
            await fault.StopAsync(token).ConfigureAwait(true);
            Step(fault.Role + "-stopped");
            await source.ServerClosed.WaitAsync(EventGuard, token).ConfigureAwait(true);
            var error = await Assert
                .ThrowsAsync<ZooKeeperException>(async () =>
                {
                    _ = await uncertain.ConfigureAwait(true);
                })
                .ConfigureAwait(true);
            Assert.Equal(RequestOutcome.Unknown, error.Outcome);
            Assert.IsNotType<ZooKeeperTimeoutException>(error);
            Step("unknown-after-upstream-loss");
            if (fault.Role == MemberControl.Leader)
            {
                replacement = await fault.WaitForReplacementLeaderAsync(token).ConfigureAwait(true);
                Step("replacement-leader-" + replacement);
            }
            await WaitStateAsync(stateReader, ZooKeeperSessionState.Disconnected, token)
                .ConfigureAwait(true);
            var ready = owner.WaitForConnectionAsync(token);
            await Task.WhenAny(first.RestoreHeld, second.RestoreHeld)
                .WaitAsync(EventGuard, token)
                .ConfigureAwait(true);
            Assert.Equal(ZooKeeperSessionState.Restoring, owner.Session.State);
            Assert.False(ready.IsCompleted);
            Assert.False(nextGap.IsCompleted);
            var denied = await Assert
                .ThrowsAsync<ZooKeeperException>(() => owner.SyncAsync(_fixture.Root, token))
                .ConfigureAwait(true);
            Assert.Equal(RequestOutcome.NotSent, denied.Outcome);
            Step("restore-held-admission-closed");
            await control
                .WaitForConnectionAsync(token)
                .WaitAsync(EventGuard, token)
                .ConfigureAwait(true);
            await control
                .SetDataAsync(oncePath, new byte[] { 2 }, ZNodeVersion.Any, token)
                .ConfigureAwait(true);
            first.ReleaseRestore();
            second.ReleaseRestore();
            await ready.WaitAsync(EventGuard, token).ConfigureAwait(true);
            Assert.Equal(session, owner.Session.SessionId);
            Assert.True(owner.Session.ConnectionGeneration > generation);
            Assert.True(await nextGap.WaitAsync(EventGuard, token).ConfigureAwait(true));
            Assert.Equal(ZooKeeperWatchEventKind.ReconnectedWithGap, events.Current.Kind);
            Assert.True(await nextOnce.WaitAsync(EventGuard, token).ConfigureAwait(true));
            Assert.Equal(ZooKeeperWatchEventKind.NodeDataChanged, onceEvents.Current.Kind);
            Assert.False(await onceEvents.MoveNextAsync().ConfigureAwait(true));
            await control
                .SetDataAsync(persistentPath, new byte[] { 3 }, ZNodeVersion.Any, token)
                .ConfigureAwait(true);
            Assert.True(
                await events.MoveNextAsync().WaitAsync(EventGuard, token).ConfigureAwait(true)
            );
            Assert.Equal(ZooKeeperWatchEventKind.NodeDataChanged, events.Current.Kind);
            Assert.Equal(
                (byte)3,
                Assert.Single(
                    (
                        await owner.GetDataAsync(persistentPath, token).ConfigureAwait(true)
                    ).Data!.Value.ToArray()
                )
            );
            var retained = await control.ExistsAsync(ephemeralPath, token).ConfigureAwait(true);
            Assert.NotNull(retained);
            Assert.Equal(session, retained.GetValueOrDefault().EphemeralOwner);
            Assert.Single(
                (
                    await control.GetChildrenAsync(_fixture.Root, token).ConfigureAwait(true)
                ).Children,
                static child => child.StartsWith(AttemptPrefix, StringComparison.Ordinal)
            );
            Assert.Equal(createsBefore + 1, source.CreateTransmissions);
            Assert.Equal(0, first.CreateTransmissions + second.CreateTransmissions);
            Step("same-session-restored-no-create-replay");
            await owner.CloseAsync(token).ConfigureAwait(true);
            Assert.Equal(ZooKeeperCloseStatus.Acknowledged, owner.Session.CloseStatus);
            await control.SyncAsync(_fixture.Root, token).ConfigureAwait(true);
            Assert.Null(await control.ExistsAsync(ephemeralPath, token).ConfigureAwait(true));
            Step("acknowledged-close");
            await capture
                .WaitForAsync(static activity => activity.Name == "zklink.close", token)
                .ConfigureAwait(true);
            capture.Collect();
            Assert.Contains(
                capture.Activities,
                static activity =>
                    activity.Tags.Any(static tag =>
                        tag.Key == "outcome" && Equals(tag.Value, "Unknown")
                    )
            );
            Assert.True(capture.Value("zklink.reconnect.attempts") >= 1);
            Assert.Contains(
                capture.Histograms,
                static value => value.Name == "zklink.session.disconnected.duration"
            );
            capture.AssertRedacted(_fixture.Root);
            foreach (var activity in capture.Activities)
            {
                TestContext.Current.TestOutputHelper?.WriteLine(
                    FormattableString.Invariant(
                        $"telemetry {activity.Name} duration-ms={activity.Duration.TotalMilliseconds:F3} status={activity.Status} "
                    )
                        + string.Join(
                            ',',
                            activity.Tags.Select(static tag => tag.Key + "=" + tag.Value)
                        )
                );
            }
            foreach (
                string name in new[]
                {
                    "zklink.reconnect.attempts",
                    "zklink.operation.unknown",
                    "zklink.requests.outstanding",
                    "zklink.requests.tombstones",
                    "zklink.events.backlog",
                    "zklink.telemetry.dropped",
                }
            )
            {
                TestContext.Current.TestOutputHelper?.WriteLine(
                    $"metric {name}={capture.Value(name)}"
                );
            }
        }
        finally
        {
            first.ReleaseRestore();
            second.ReleaseRestore();
            using var rollback = new CancellationTokenSource(RollbackGuard);
            try
            {
                await fault.RestoreAsync(rollback.Token).ConfigureAwait(true);
                if (replacement is not null)
                {
                    Assert.Equal(
                        replacement,
                        await fault
                            .WaitForReplacementLeaderAsync(rollback.Token)
                            .ConfigureAwait(true)
                    );
                }
                Step(fault.Role + "-restored-as-follower-authenticated-readiness");
                Step("fixture-cleanup-state-" + _fixture.Client.Session.State);
                await _fixture.Client.WaitForConnectionAsync(rollback.Token).ConfigureAwait(true);
                Step("fixture-cleanup-ready");
            }
            finally
            {
                await owner.DisposeAsync().ConfigureAwait(true);
                if (uncertain is not null)
                {
                    try
                    {
                        await uncertain.ConfigureAwait(true);
                    }
                    catch (Exception error)
                        when (error is ZooKeeperException or OperationCanceledException) { }
                }
            }
        }
    }

    private static int Port(string endpoint) => new Uri("tcp://" + endpoint).Port;

    private static async Task WaitStateAsync(
        EventProbe<ZooKeeperSessionSnapshot> reader,
        ZooKeeperSessionState state,
        CancellationToken token
    )
    {
        do
        {
            Assert.True(
                await reader.MoveNextAsync().WaitAsync(EventGuard, token).ConfigureAwait(false)
            );
        } while (reader.Current.State != state);
    }
}
