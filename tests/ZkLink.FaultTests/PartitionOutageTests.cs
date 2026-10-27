using System.Diagnostics;
using ZkLink.IntegrationTests;
using ZkLink.Testing;

namespace ZkLink.FaultTests;

[Trait("Fixture", "PartitionFault")]
public sealed class PartitionOutageTests : IAsyncLifetime
{
    private const int TestTimeoutMilliseconds = 90_000;
    private const int ReadSilenceNumerator = 2;
    private const int SessionTimerDenominator = 3;
    private const string AttemptPrefix = "attempt-";
    private static readonly TimeSpan SessionBudget = TimeSpan.FromSeconds(12);
    private static readonly TimeSpan OperationGuard = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan EventGuard = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan RecoveryAttempt = TimeSpan.FromMilliseconds(750);
    private static readonly TimeSpan SchedulingAllowance = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan PostHealObservation = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan ObservationPoll = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan CleanupGuard = TimeSpan.FromSeconds(50);
    private readonly LiveZooKeeperFixture _fixture = new();

    public ValueTask InitializeAsync()
    {
        var fault = new MemberControl();
        if (fault.Role != MemberControl.Partition)
        {
            throw new InvalidOperationException(
                "Run just zk-partition-faults with its owned fixture."
            );
        }
        return _fixture.InitializeAsync();
    }

    public ValueTask DisposeAsync() => _fixture.DisposeAsync();

    [Fact(Timeout = TestTimeoutMilliseconds)]
    [Trait("Acceptance", "T09")]
    [Trait("Acceptance", "T10")]
    [Trait("Acceptance", "T14")]
    [Trait("Acceptance", "T26")]
    [Trait("Acceptance", "T29")]
    [Trait("Acceptance", "T32")]
    public async Task IncomingSilenceIgnoresOutgoingHeartbeatsAndNeverReplaysUncertainCreate()
    {
        var clock = Stopwatch.StartNew();
        void Step(string name) =>
            TestContext.Current.TestOutputHelper?.WriteLine(
                $"{name} at {clock.ElapsedMilliseconds}ms"
            );
        var token = TestContext.Current.CancellationToken;
        var fault = new MemberControl();
        bool expectLost = fault.Outage == MemberControl.UntilLost;
        using var capture = new TelemetryCapture();
        var proxy = new FaultProxy(fault.Endpoint, holdRestore: !expectLost, token);
        await using var proxyLifetime = proxy.ConfigureAwait(true);
        var control = _fixture.Client;
        var owner = await _fixture
            .ConnectAsync(
                connectionString: proxy.Endpoint,
                sessionTimeout: SessionBudget,
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
        var once = await owner.GetDataAndWatchAsync(oncePath, token).ConfigureAwait(true);
        await using var onceLifetime = once.Watch.ConfigureAwait(true);
        var persistent = await owner
            .AddWatchAsync(persistentPath, PersistentWatchMode.Persistent, token)
            .ConfigureAwait(true);
        await using var persistentLifetime = persistent.ConfigureAwait(true);
        var events = new EventProbe<ZooKeeperWatchEvent>(
            persistent.ReadEventsAsync(token),
            token,
            EventGuard
        );
        await using var eventsLifetime = events.ConfigureAwait(true);
        var nextGap = events.MoveNextAsync();
        var onceEvents = new EventProbe<ZooKeeperWatchEvent>(
            once.Watch.ReadEventsAsync(token),
            token,
            EventGuard
        );
        await using var onceEventsLifetime = onceEvents.ConfigureAwait(true);
        var nextOnce = onceEvents.MoveNextAsync();
        var states = owner.SubscribeStateChanges();
        await using var statesLifetime = states.ConfigureAwait(true);
        var stateReader = new EventProbe<ZooKeeperSessionSnapshot>(
            states.ReadStatesAsync(token),
            token,
            EventGuard
        );
        await using var stateReaderLifetime = stateReader.ConfigureAwait(true);
        Assert.True(await stateReader.MoveNextAsync().ConfigureAwait(true));
        long session = owner.Session.SessionId;
        long generation = owner.Session.ConnectionGeneration;
        var negotiated = owner.Session.NegotiatedTimeout.GetValueOrDefault();
        Assert.True(negotiated > SchedulingAllowance && negotiated < OperationGuard);
        Step("negotiated-timeout-ms-" + negotiated.TotalMilliseconds);
        int createsBefore = proxy.CreateTransmissions;
        Task<CreateResult>? uncertain = null;
        try
        {
            proxy.ArmCreateReply();
            uncertain = owner.CreateAsync(
                _fixture.Root + "/" + AttemptPrefix,
                new byte[] { 7 },
                new CreateOptions(
                    LiveZooKeeperFixture.OwnerCreate.Acl,
                    CreateMode.PersistentSequential
                ),
                token
            );
            await proxy.ResponseWithheld.WaitAsync(EventGuard, token).ConfigureAwait(true);
            Assert.Equal(FaultProxy.Success, proxy.WithheldError);
            Assert.False(uncertain.IsCompleted);
            Assert.True(proxy.HasOpenConnection);
            Assert.False(proxy.ServerClosed.IsCompleted);
            await AssertSingleAttemptAsync().ConfigureAwait(true);
            Step("committed-reply-withheld-open-transport");
            var silence = Stopwatch.StartNew();
            int pingsBefore = proxy.ForwardedPings;
            proxy.BlockIncoming();
            Step("incoming-partition-enabled");
            await WaitStateAsync(ZooKeeperSessionState.Disconnected).ConfigureAwait(true);
            var readSilence = negotiated * ReadSilenceNumerator / SessionTimerDenominator;
            Assert.InRange(
                silence.Elapsed,
                readSilence - SchedulingAllowance,
                readSilence + SchedulingAllowance
            );
            Assert.True(proxy.ForwardedPings > pingsBefore);
            Assert.True(proxy.DroppedIncomingFrames > 0);
            Assert.False(proxy.ServerClosed.IsCompleted);
            Assert.Null(proxy.RunFailure);
            Step("disconnected-from-incoming-silence-with-forwarded-pings");
            var error = await Assert
                .ThrowsAsync<ZooKeeperException>(async () =>
                {
                    _ = await uncertain.ConfigureAwait(true);
                })
                .ConfigureAwait(true);
            Assert.Equal(RequestOutcome.Unknown, error.Outcome);
            Assert.IsNotType<ZooKeeperTimeoutException>(error);
            var ready = owner.WaitForConnectionAsync(token);
            Assert.False(ready.IsCompleted);
            Assert.False(nextGap.IsCompleted);
            var denied = await Assert
                .ThrowsAsync<ZooKeeperException>(() => owner.SyncAsync(_fixture.Root, token))
                .ConfigureAwait(true);
            Assert.Equal(RequestOutcome.NotSent, denied.Outcome);
            if (expectLost)
            {
                var lost = await Assert
                    .ThrowsAsync<ZooKeeperSessionException>(async () =>
                    {
                        _ = await nextGap.ConfigureAwait(true);
                    })
                    .ConfigureAwait(true);
                Assert.Equal(ZooKeeperSessionState.Lost, lost.State);
                Assert.Null(lost.ErrorCode);
                Assert.InRange(
                    silence.Elapsed,
                    negotiated - SchedulingAllowance,
                    negotiated + SchedulingAllowance
                );
                var onceLost = await Assert
                    .ThrowsAsync<ZooKeeperSessionException>(async () =>
                    {
                        _ = await nextOnce.ConfigureAwait(true);
                    })
                    .ConfigureAwait(true);
                Assert.Equal(ZooKeeperSessionState.Lost, onceLost.State);
                await Assert
                    .ThrowsAsync<ZooKeeperSessionException>(() => ready)
                    .ConfigureAwait(true);
                long terminalGeneration = owner.Session.ConnectionGeneration;
                int terminalAttempts = proxy.ConnectionAttempts;
                Step("local-lost-without-server-expiry-claim");
                proxy.HealIncoming();
                Step("incoming-partition-healed-after-local-loss");
                var observation = Stopwatch.StartNew();
                do
                {
                    await control.SyncAsync(_fixture.Root, token).ConfigureAwait(true);
                    Assert.Equal(ZooKeeperSessionState.Lost, owner.Session.State);
                    Assert.Equal(terminalAttempts, proxy.ConnectionAttempts);
                    await Task.Delay(ObservationPoll, token).ConfigureAwait(true);
                } while (observation.Elapsed < PostHealObservation);
                Assert.Equal(terminalAttempts, proxy.ConnectionAttempts);
                Assert.Equal(session, owner.Session.SessionId);
                Assert.Equal(terminalGeneration, owner.Session.ConnectionGeneration);
                await Assert
                    .ThrowsAsync<ZooKeeperSessionException>(() =>
                        owner.WaitForConnectionAsync(token)
                    )
                    .ConfigureAwait(true);
                var terminal = await Assert
                    .ThrowsAsync<ZooKeeperException>(() => owner.SyncAsync(_fixture.Root, token))
                    .ConfigureAwait(true);
                Assert.Equal(RequestOutcome.NotSent, terminal.Outcome);
                Step("terminal-owner-not-revived-after-heal");
            }
            else
            {
                proxy.HealIncoming();
                Step("incoming-partition-healed-before-local-loss");
                await Task.WhenAny(proxy.RestoreHeld, ready)
                    .WaitAsync(EventGuard, token)
                    .ConfigureAwait(true);
                Assert.Equal(ZooKeeperSessionState.Restoring, owner.Session.State);
                Assert.False(ready.IsCompleted);
                Assert.False(nextGap.IsCompleted);
                var restoring = await Assert
                    .ThrowsAsync<ZooKeeperException>(() => owner.SyncAsync(_fixture.Root, token))
                    .ConfigureAwait(true);
                Assert.Equal(RequestOutcome.NotSent, restoring.Outcome);
                await control
                    .SetDataAsync(oncePath, new byte[] { 2 }, ZNodeVersion.Any, token)
                    .ConfigureAwait(true);
                proxy.ReleaseRestore();
                await ready.WaitAsync(EventGuard, token).ConfigureAwait(true);
                Assert.Equal(session, owner.Session.SessionId);
                Assert.True(owner.Session.ConnectionGeneration > generation);
                Assert.True(await nextGap.ConfigureAwait(true));
                Assert.Equal(ZooKeeperWatchEventKind.ReconnectedWithGap, events.Current.Kind);
                Assert.True(await nextOnce.ConfigureAwait(true));
                Assert.Equal(ZooKeeperWatchEventKind.NodeDataChanged, onceEvents.Current.Kind);
                Assert.False(await onceEvents.MoveNextAsync().ConfigureAwait(true));
                await control
                    .SetDataAsync(persistentPath, new byte[] { 3 }, ZNodeVersion.Any, token)
                    .ConfigureAwait(true);
                Assert.True(await events.MoveNextAsync().ConfigureAwait(true));
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
                await owner.CloseAsync(token).ConfigureAwait(true);
                Assert.Equal(ZooKeeperCloseStatus.Acknowledged, owner.Session.CloseStatus);
                await control.SyncAsync(_fixture.Root, token).ConfigureAwait(true);
                Assert.Null(await control.ExistsAsync(ephemeralPath, token).ConfigureAwait(true));
                Step("same-session-watches-and-acknowledged-close");
            }
            await AssertSingleAttemptAsync().ConfigureAwait(true);
            Assert.Equal(createsBefore + 1, proxy.CreateTransmissions);
            capture.Collect();
            capture.AssertRedacted(_fixture.Root);
            TestContext.Current.TestOutputHelper?.WriteLine(
                $"partition counters forwarded-pings={proxy.ForwardedPings} dropped-incoming={proxy.DroppedIncomingFrames} connection-attempts={proxy.ConnectionAttempts}"
            );
            Step("uncertain-create-never-replayed");
        }
        catch (Exception failure)
        {
            TestContext.Current.TestOutputHelper?.WriteLine(
                $"failure type={failure.GetType().Name} owner-state={owner.Session.State} pings={proxy.ForwardedPings} dropped={proxy.DroppedIncomingFrames} proxy-failure={proxy.RunFailure}"
            );
            throw;
        }
        finally
        {
            proxy.HealIncoming();
            proxy.ReleaseRestore();
            Step("incoming-partition-healed-in-finally");
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
            using var cleanup = new CancellationTokenSource(CleanupGuard);
            var probe = await _fixture
                .ConnectAsync(connectionString: proxy.Endpoint, cancellationToken: cleanup.Token)
                .ConfigureAwait(true);
            await using var probeLifetime = probe.ConfigureAwait(true);
            Assert.False(proxy.IncomingPartitionActive);
            await probe.SyncAsync(_fixture.Root, cleanup.Token).ConfigureAwait(true);
            await probe.CloseAsync(cleanup.Token).ConfigureAwait(true);
            Assert.Equal(ZooKeeperCloseStatus.Acknowledged, probe.Session.CloseStatus);
            Step("healed-proxy-authenticated-sync-close");
        }

        async Task AssertSingleAttemptAsync()
        {
            await control.SyncAsync(_fixture.Root, token).ConfigureAwait(true);
            Assert.Single(
                (
                    await control.GetChildrenAsync(_fixture.Root, token).ConfigureAwait(true)
                ).Children,
                static child => child.StartsWith(AttemptPrefix, StringComparison.Ordinal)
            );
        }

        async Task WaitStateAsync(ZooKeeperSessionState state)
        {
            do
            {
                Assert.True(await stateReader.MoveNextAsync().ConfigureAwait(true));
            } while (stateReader.Current.State != state);
        }
    }
}
