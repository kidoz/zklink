using System.Diagnostics;
using ZkLink.IntegrationTests;
using ZkLink.Testing;

namespace ZkLink.FaultTests;

[Trait("Fixture", "MemberFault")]
public sealed class MemberOutageTests : IAsyncLifetime
{
    private const int TestTimeoutMilliseconds = 90_000;
    private const string AttemptPrefix = "attempt-";
    private static readonly TimeSpan EventGuard = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan RecoveryAttempt = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan OperationGuard = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan RollbackGuard = TimeSpan.FromSeconds(50);
    private static readonly TimeSpan ShortSession = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan QuorumReadGuard = TimeSpan.FromSeconds(35);
    private static readonly TimeSpan PeerOperationGuard = TimeSpan.FromSeconds(35);
    private static readonly TimeSpan MinorityWriteGuard = TimeSpan.FromSeconds(3);
    private readonly LiveZooKeeperFixture _fixture = new();

    public ValueTask InitializeAsync()
    {
        var fault = new MemberControl(); // Reject missing/foreign fixture metadata before opening any client.
        if (fault.Role == MemberControl.Partition)
        {
            throw new InvalidOperationException(
                "The member fault selection must exclude partition cases."
            );
        }
        return _fixture.InitializeAsync();
    }

    public ValueTask DisposeAsync() => _fixture.DisposeAsync();

    [Fact(Timeout = TestTimeoutMilliseconds)]
    [Trait("Acceptance", "T32")]
    [Trait("Acceptance", "T14")]
    [Trait("Acceptance", "T26")]
    [Trait("Acceptance", "T34")]
    public async Task MemberFaultsPreserveValidSessionsOrReportLossWithoutReplayingUncertainCreate()
    {
        var clock = Stopwatch.StartNew();
        void Step(string name) =>
            TestContext.Current.TestOutputHelper?.WriteLine(
                $"{name} at {clock.ElapsedMilliseconds}ms"
            );
        var token = TestContext.Current.CancellationToken;
        var fault = new MemberControl();
        bool expectLost =
            fault.Role == MemberControl.Quorum && fault.Outage == MemberControl.UntilLost;
        string[] survivors = _fixture
            .ConnectionString.Split(',')
            .Where(endpoint => endpoint != fault.Endpoint)
            .ToArray();
        Assert.Equal(2, survivors.Length);
        string[] maintenanceEndpoints =
            fault.Role == MemberControl.Quorum ? _fixture.ConnectionString.Split(',') : survivors;
        await _fixture
            .Client.UpdateEndpointsAsync(
                maintenanceEndpoints
                    .Select(static endpoint => new ZooKeeperEndpoint("127.0.0.1", Port(endpoint)))
                    .ToArray(),
                token
            )
            .ConfigureAwait(true);
        using var capture = new TelemetryCapture();
        var source = new FaultProxy(fault.Endpoint, holdRestore: false, token);
        await using var sourceLifetime = source.ConfigureAwait(true);
        var first = new FaultProxy(survivors[0], holdRestore: !expectLost, token);
        await using var firstLifetime = first.ConfigureAwait(true);
        var second = new FaultProxy(survivors[1], holdRestore: !expectLost, token);
        await using var secondLifetime = second.ConfigureAwait(true);
        var control = await _fixture
            .ConnectAsync(connectionString: survivors[0])
            .ConfigureAwait(true);
        await using var controlLifetime = control.ConfigureAwait(true);
        if (fault.Role == MemberControl.Quorum)
        {
            await control
                .UpdateEndpointsAsync(
                    maintenanceEndpoints
                        .Select(static endpoint => new ZooKeeperEndpoint(
                            "127.0.0.1",
                            Port(endpoint)
                        ))
                        .ToArray(),
                    token
                )
                .ConfigureAwait(true);
        }
        var owner = await _fixture
            .ConnectAsync(
                connectionString: source.Endpoint,
                sessionTimeout: expectLost ? ShortSession : null,
                connectAttemptTimeout: RecoveryAttempt,
                operationTimeout: fault.Role == MemberControl.Peer
                    ? PeerOperationGuard
                    : OperationGuard
            )
            .ConfigureAwait(true);
        await using var ownerLifetime = owner.ConfigureAwait(true);
        string oncePath = _fixture.Root + "/once";
        string persistentPath = _fixture.Root + "/persistent";
        string ephemeralPath = _fixture.Root + "/lease";
        string majorityPath = _fixture.Root + "/majority-progress";
        string minorityPath = _fixture.Root + "/minority-probe";
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
        TimeSpan restorationGuard =
            fault.Role is MemberControl.Quorum or MemberControl.Peer && !expectLost
                ? QuorumReadGuard
                : EventGuard;
        var events = new EventProbe<ZooKeeperWatchEvent>(
            persistent.ReadEventsAsync(token),
            token,
            restorationGuard
        );
        await using var eventsLifetime = events.ConfigureAwait(true);
        var nextGap = events.MoveNextAsync();
        var onceEvents = new EventProbe<ZooKeeperWatchEvent>(
            oneShot.Watch.ReadEventsAsync(token),
            token,
            fault.Role == MemberControl.Peer ? QuorumReadGuard : EventGuard
        );
        await using var onceEventsLifetime = onceEvents.ConfigureAwait(true);
        Task<bool>? nextOnce =
            fault.Role == MemberControl.Quorum && !expectLost ? null : onceEvents.MoveNextAsync();
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
        Assert.NotNull(owner.Session.NegotiatedTimeout);
        Step("negotiated-timeout-ms-" + owner.Session.NegotiatedTimeout.Value.TotalMilliseconds);
        if (expectLost)
        {
            Assert.True(owner.Session.NegotiatedTimeout <= EventGuard);
        }
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
        ZooKeeperClient? recoveredControl = null;
        ZooKeeperClient? minorityProbe = null;
        bool majorityCommitted = false;
        string? replacement = null;
        try
        {
            if (fault.Role == MemberControl.Peer)
            {
                minorityProbe = await _fixture
                    .ConnectAsync(
                        connectionString: fault.Endpoint,
                        operationTimeout: MinorityWriteGuard
                    )
                    .ConfigureAwait(true);
                await control
                    .CreateAsync(
                        minorityPath,
                        new byte[] { 1 },
                        LiveZooKeeperFixture.OwnerCreate,
                        token
                    )
                    .ConfigureAwait(true);
            }
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
            Step(
                fault.Role == MemberControl.Peer
                    ? "peer-isolation-requested"
                    : fault.Role + "-stop-requested"
            );
            if (fault.Role == MemberControl.Peer)
            {
                await fault.PartitionPeerAsync(token).ConfigureAwait(true);
                Step("peer-interface-isolated");
                if (fault.PeerTargetRole == MemberControl.Leader)
                {
                    replacement = await fault
                        .WaitForReplacementLeaderAsync(token)
                        .ConfigureAwait(true);
                    Step("peer-replacement-leader-" + replacement);
                    await control.WaitForConnectionAsync(token).ConfigureAwait(true);
                }
                await control
                    .CreateAsync(
                        majorityPath,
                        new byte[] { 3 },
                        LiveZooKeeperFixture.OwnerCreate,
                        token
                    )
                    .ConfigureAwait(true);
                majorityCommitted = true;
                await control.SyncAsync(_fixture.Root, token).ConfigureAwait(true);
                var minorityFailure = await Assert
                    .ThrowsAnyAsync<ZooKeeperException>(() =>
                        minorityProbe!.SetDataAsync(
                            minorityPath,
                            new byte[] { 2 },
                            ZNodeVersion.Any,
                            token
                        )
                    )
                    .ConfigureAwait(true);
                Assert.True(
                    minorityFailure.Outcome is RequestOutcome.Unknown or RequestOutcome.NotSent
                );
                await control.SyncAsync(_fixture.Root, token).ConfigureAwait(true);
                Assert.Equal(
                    (byte)1,
                    Assert.Single(
                        (
                            await control.GetDataAsync(minorityPath, token).ConfigureAwait(true)
                        ).Data!.Value.ToArray()
                    )
                );
                Step("majority-committed-minority-write-" + minorityFailure.Outcome);
                await minorityProbe!.DisposeAsync().ConfigureAwait(true);
                minorityProbe = null;
            }
            else
            {
                await fault.StopAsync(token).ConfigureAwait(true);
                Step(fault.Role + "-stopped");
            }
            if (fault.Role == MemberControl.Peer)
            {
                // A live isolated server may race its close with the client's outgoing pump.
                await WaitStateAsync(stateReader, ZooKeeperSessionState.Disconnected, token)
                    .ConfigureAwait(true);
            }
            else
            {
                await source.ServerClosed.WaitAsync(EventGuard, token).ConfigureAwait(true);
            }
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
            if (fault.Role != MemberControl.Peer)
            {
                await WaitStateAsync(stateReader, ZooKeeperSessionState.Disconnected, token)
                    .ConfigureAwait(true);
            }
            var ready = owner.WaitForConnectionAsync(token);
            if (fault.Role == MemberControl.Quorum)
            {
                Assert.False(ready.IsCompleted);
                Assert.False(nextGap.IsCompleted);
                var unavailable = await Assert
                    .ThrowsAsync<ZooKeeperException>(() => owner.SyncAsync(_fixture.Root, token))
                    .ConfigureAwait(true);
                Assert.Equal(RequestOutcome.NotSent, unavailable.Outcome);
                Step("quorum-unavailable-admission-closed");
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
                    var onceLost = await Assert
                        .ThrowsAsync<ZooKeeperSessionException>(async () =>
                        {
                            _ = await nextOnce!.ConfigureAwait(true);
                        })
                        .ConfigureAwait(true);
                    Assert.Equal(ZooKeeperSessionState.Lost, onceLost.State);
                    await Assert
                        .ThrowsAsync<ZooKeeperSessionException>(() => ready)
                        .ConfigureAwait(true);
                    Assert.Equal(ZooKeeperSessionState.Lost, owner.Session.State);
                    long terminalGeneration = owner.Session.ConnectionGeneration;
                    int attemptsAtLoss = first.ConnectionAttempts + second.ConnectionAttempts;
                    Step("local-lost-without-server-expiry-claim");
                    await fault.RecoverQuorumAsync(token).ConfigureAwait(true);
                    Step("quorum-restored-after-local-loss");
                    await RefreshControlAsync().ConfigureAwait(true);
                    await VerifyNoReplayAsync().ConfigureAwait(true);
                    Assert.Equal(ZooKeeperSessionState.Lost, owner.Session.State);
                    Assert.Equal(session, owner.Session.SessionId);
                    Assert.Equal(terminalGeneration, owner.Session.ConnectionGeneration);
                    Assert.Equal(
                        attemptsAtLoss,
                        first.ConnectionAttempts + second.ConnectionAttempts
                    );
                    await Assert
                        .ThrowsAsync<ZooKeeperSessionException>(() =>
                            owner.WaitForConnectionAsync(token)
                        )
                        .ConfigureAwait(true);
                    var terminal = await Assert
                        .ThrowsAsync<ZooKeeperException>(() =>
                            owner.SyncAsync(_fixture.Root, token)
                        )
                        .ConfigureAwait(true);
                    Assert.Equal(RequestOutcome.NotSent, terminal.Outcome);
                    capture.Collect();
                    capture.AssertRedacted(_fixture.Root);
                    Step("terminal-client-not-revived-no-create-replay");
                    return;
                }
                await fault.RecoverQuorumAsync(token).ConfigureAwait(true);
                Step("quorum-restored-before-session-loss");
            }
            await Task.WhenAny(first.RestoreHeld, second.RestoreHeld, ready)
                .WaitAsync(restorationGuard, token)
                .ConfigureAwait(true);
            Assert.Equal(ZooKeeperSessionState.Restoring, owner.Session.State);
            Assert.False(ready.IsCompleted);
            Assert.False(nextGap.IsCompleted);
            var denied = await Assert
                .ThrowsAsync<ZooKeeperException>(() => owner.SyncAsync(_fixture.Root, token))
                .ConfigureAwait(true);
            Assert.Equal(RequestOutcome.NotSent, denied.Outcome);
            Step("restore-held-admission-closed");
            if (fault.Role != MemberControl.Quorum)
            {
                await MutateOneShotAsync().ConfigureAwait(true);
            }
            first.ReleaseRestore();
            second.ReleaseRestore();
            await ready.WaitAsync(EventGuard, token).ConfigureAwait(true);
            Assert.Equal(session, owner.Session.SessionId);
            Assert.True(owner.Session.ConnectionGeneration > generation);
            Assert.True(await nextGap.WaitAsync(EventGuard, token).ConfigureAwait(true));
            Assert.Equal(ZooKeeperWatchEventKind.ReconnectedWithGap, events.Current.Kind);
            if (fault.Role == MemberControl.Quorum)
            {
                await RefreshControlAsync().ConfigureAwait(true);
                nextOnce = onceEvents.MoveNextAsync();
                await MutateOneShotAsync().ConfigureAwait(true);
            }
            Assert.True(await nextOnce!.WaitAsync(EventGuard, token).ConfigureAwait(true));
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
            await VerifyNoReplayAsync().ConfigureAwait(true);
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
        catch (Exception failure)
        {
            TestContext.Current.TestOutputHelper?.WriteLine(
                $"failure type={failure.GetType().Name} outcome={(failure as ZooKeeperException)?.Outcome} error={(failure as ZooKeeperException)?.ErrorCode} owner-state={owner.Session.State} control-state={control.Session.State} fixture-state={_fixture.Client.Session.State}"
            );
            foreach (var proxy in new[] { first, second })
            {
                TestContext.Current.TestOutputHelper?.WriteLine(
                    $"recovery-proxy attempts={proxy.ConnectionAttempts} handshakes={proxy.HandshakeReplies} negotiated-ms={proxy.LastNegotiatedMilliseconds} restore-held={proxy.RestoreHeld.IsCompleted} failure={proxy.RunFailure}"
                );
            }
            throw;
        }
        finally
        {
            first.ReleaseRestore();
            second.ReleaseRestore();
            using var rollback = new CancellationTokenSource(RollbackGuard);
            try
            {
                await fault.RestoreAsync(rollback.Token).ConfigureAwait(true);
                if (fault.Role == MemberControl.Peer && majorityCommitted)
                {
                    foreach (string endpoint in _fixture.ConnectionString.Split(','))
                    {
                        var replica = await _fixture
                            .ConnectAsync(
                                connectionString: endpoint,
                                cancellationToken: rollback.Token
                            )
                            .ConfigureAwait(true);
                        await using var replicaLifetime = replica.ConfigureAwait(true);
                        await replica.SyncAsync(_fixture.Root, rollback.Token).ConfigureAwait(true);
                        var data = await replica
                            .GetDataAsync(majorityPath, rollback.Token)
                            .ConfigureAwait(true);
                        Assert.Equal((byte)3, Assert.Single(data.Data!.Value.ToArray()));
                    }
                    Step("peer-healed-all-replicas-consistent");
                }
                if (replacement is not null)
                {
                    Assert.Equal(
                        replacement,
                        await fault
                            .WaitForReplacementLeaderAsync(rollback.Token)
                            .ConfigureAwait(true)
                    );
                }
                Step(fault.Role + "-restored-authenticated-readiness");
                if (fault.Role == MemberControl.Quorum)
                {
                    await _fixture.RefreshCleanupClientAsync(rollback.Token).ConfigureAwait(true);
                }
                Step("fixture-cleanup-state-" + _fixture.Client.Session.State);
                await _fixture.Client.WaitForConnectionAsync(rollback.Token).ConfigureAwait(true);
                Step("fixture-cleanup-ready");
            }
            finally
            {
                await owner.DisposeAsync().ConfigureAwait(true);
                if (minorityProbe is not null)
                {
                    await minorityProbe.DisposeAsync().ConfigureAwait(true);
                }
                if (recoveredControl is not null)
                {
                    await recoveredControl.DisposeAsync().ConfigureAwait(true);
                }
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

        async Task RefreshControlAsync()
        {
            await fault.RestoreAsync(token).ConfigureAwait(true);
            await control.DisposeAsync().ConfigureAwait(true);
            recoveredControl = await _fixture.ConnectAsync().ConfigureAwait(true);
            control = recoveredControl;
            Step("all-members-healthy-independent-control-connected");
        }

        async Task MutateOneShotAsync()
        {
            await control
                .WaitForConnectionAsync(token)
                .WaitAsync(EventGuard, token)
                .ConfigureAwait(true);
            await control
                .SetDataAsync(oncePath, new byte[] { 2 }, ZNodeVersion.Any, token)
                .ConfigureAwait(true);
        }

        async Task VerifyNoReplayAsync()
        {
            await control
                .WaitForConnectionAsync(token)
                .WaitAsync(EventGuard, token)
                .ConfigureAwait(true);
            await control.SyncAsync(_fixture.Root, token).ConfigureAwait(true);
            Assert.Single(
                (
                    await control.GetChildrenAsync(_fixture.Root, token).ConfigureAwait(true)
                ).Children,
                static child => child.StartsWith(AttemptPrefix, StringComparison.Ordinal)
            );
            Assert.Equal(createsBefore + 1, source.CreateTransmissions);
            Assert.Equal(0, first.CreateTransmissions + second.CreateTransmissions);
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
