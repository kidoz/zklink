using System.Diagnostics;
using System.Globalization;
using ZkLink.IntegrationTests;

namespace ZkLink.FaultTests;

[Collection("WatchPressure")]
[Trait("Fixture", "Soak")]
public sealed class SoakTests : IAsyncLifetime
{
    private const int MaximumConcurrency = 4;
    private const int MinimumJobs = 2;
    private const int MaximumJobs = 8;
    private const int MinimumPayloadBytes = 32;
    private const int MaximumPayloadBytes = 256;
    private const string AttemptPrefix = "attempt-";
    private static readonly TimeSpan RoundGuard = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan CleanupGuard = TimeSpan.FromSeconds(15);
    private readonly SoakSettings _settings = SoakSettings.Read();
    private readonly LiveZooKeeperFixture _fixture = new();
    private static string Endpoint =>
        Environment.GetEnvironmentVariable("ZKLINK_FAULT_ENDPOINT")
        ?? throw new InvalidOperationException("The owned soak forwarding target is required.");

    public ValueTask InitializeAsync()
    {
        _ = Endpoint;
        return _fixture.InitializeAsync();
    }

    public ValueTask DisposeAsync() => _fixture.DisposeAsync();

    [Fact(Timeout = SoakSettings.MaximumTestMilliseconds)]
    [Trait("Acceptance", "T33")]
    [Trait("Acceptance", "T14")]
    [Trait("Acceptance", "T26")]
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Security",
        "CA5394",
        Justification = "Seeded Random controls reproducible non-secret workload choices only; fixture credentials use cryptographic randomness."
    )]
    public async Task SeededChurnCancellationAndRecoveryRemainBounded()
    {
        var token = TestContext.Current.CancellationToken;
        using var metrics = new SoakMetrics();
        var evidence = new SoakEvidence(_settings);
        await using var evidenceLifetime = evidence.ConfigureAwait(true);
        var totals = new SoakTotals();
        var random = new Random(_settings.Seed);
        var elapsed = Stopwatch.StartNew();
        TimeSpan nextSample = TimeSpan.MaxValue;
        TestContext.Current.TestOutputHelper?.WriteLine(
            FormattableString.Invariant(
                $"soak-start seed={_settings.Seed} seconds={_settings.Seconds} concurrency={MaximumConcurrency}"
            )
        );
        try
        {
            await metrics.QuiescentAsync(token).ConfigureAwait(true);
            while (
                elapsed.Elapsed < TimeSpan.FromSeconds(_settings.Seconds)
                || totals.Rounds <= SoakEvidence.WarmupRounds
            )
            {
                var jobs = Enumerable
                    .Range(0, random.Next(MinimumJobs, MaximumJobs + 1))
                    .Select(_ => Plan(random))
                    .ToArray();
                await GuardedRoundAsync(jobs, totals, token).ConfigureAwait(true);
                await metrics.QuiescentAsync(token).ConfigureAwait(true);
                totals.Rounds++;
                if (totals.Rounds == SoakEvidence.WarmupRounds)
                {
                    await evidence
                        .SampleAsync("baseline", totals, elapsed.Elapsed, token)
                        .ConfigureAwait(true);
                    nextSample = elapsed.Elapsed + evidence.SampleInterval;
                }
                else if (elapsed.Elapsed >= nextSample)
                {
                    await evidence
                        .SampleAsync("checkpoint", totals, elapsed.Elapsed, token)
                        .ConfigureAwait(true);
                    nextSample = elapsed.Elapsed + evidence.SampleInterval;
                }
            }
            Assert.Equal(totals.CrudStarted, totals.CrudSucceeded);
            Assert.Equal(totals.Rounds, totals.NotSent);
            Assert.Equal(totals.Rounds, totals.Unknown);
            Assert.Equal(totals.Rounds, totals.Reconnects);
            await evidence
                .SampleAsync("summary", totals, elapsed.Elapsed, token)
                .ConfigureAwait(true);
        }
        catch (Exception failure)
        {
            await evidence.FailureAsync(failure, totals).ConfigureAwait(true);
            throw;
        }
    }

    private sealed record WorkItem(byte[] Initial, byte[] Updated, bool ReadBeforeWrite);

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Security",
        "CA5394",
        Justification = "Reproducible non-secret test payloads and operation choices require the recorded seed; this never generates credentials."
    )]
    private static WorkItem Plan(Random random)
    {
        var initial = new byte[random.Next(MinimumPayloadBytes, MaximumPayloadBytes + 1)];
        var updated = new byte[random.Next(MinimumPayloadBytes, MaximumPayloadBytes + 1)];
        random.NextBytes(initial);
        random.NextBytes(updated);
        return new(initial, updated, random.Next(2) == 0);
    }

    private async Task GuardedRoundAsync(
        WorkItem[] jobs,
        SoakTotals totals,
        CancellationToken token
    )
    {
        using var round = CancellationTokenSource.CreateLinkedTokenSource(token);
        round.CancelAfter(RoundGuard);
        await RoundAsync(jobs, totals, round.Token).ConfigureAwait(true);
    }

    private async Task RoundAsync(WorkItem[] jobs, SoakTotals totals, CancellationToken token)
    {
        string root = _fixture.Root + "/r-" + totals.Rounds.ToString(CultureInfo.InvariantCulture);
        FaultProxy? source = null;
        FaultProxy? recovery = null;
        ZooKeeperClient? owner = null;
        bool sourceDisposed = false;
        Task<CreateResult>? uncertain = null;
        var listenerPorts = new List<int>();
        var upstreamPorts = new List<int>();
        try
        {
            source = new FaultProxy(Endpoint, holdRestore: false, token);
            listenerPorts.Add(Port(source));
            recovery = new FaultProxy(Endpoint, holdRestore: true, token);
            listenerPorts.Add(Port(recovery));
            await _fixture
                .Client.CreateAsync(root, null, LiveZooKeeperFixture.OwnerCreate, token)
                .ConfigureAwait(true);
            owner = await _fixture
                .ConnectAsync(connectionString: source.Endpoint, cancellationToken: token)
                .ConfigureAwait(true);
            upstreamPorts.Add(source.UpstreamLocalPort);
            long session = owner.Session.SessionId;
            long generation = owner.Session.ConnectionGeneration;
            for (int index = 0; index < jobs.Length; index += MaximumConcurrency)
            {
                var tasks = jobs.Skip(index)
                    .Take(MaximumConcurrency)
                    .Select(
                        (job, offset) =>
                            CrudAsync(
                                owner,
                                root
                                    + "/n-"
                                    + (index + offset).ToString(CultureInfo.InvariantCulture),
                                job,
                                totals,
                                token
                            )
                    )
                    .ToArray();
                await Task.WhenAll(tasks).ConfigureAwait(true);
            }
            using (var canceled = new CancellationTokenSource())
            {
                await canceled.CancelAsync().ConfigureAwait(true);
                var denied = await Assert
                    .ThrowsAsync<ZooKeeperOperationCanceledException>(() =>
                        owner.CreateAsync(
                            root + "/not-sent",
                            null,
                            LiveZooKeeperFixture.OwnerCreate,
                            canceled.Token
                        )
                    )
                    .ConfigureAwait(true);
                Assert.Equal(RequestOutcome.NotSent, denied.Outcome);
                Assert.Null(
                    await _fixture
                        .Client.ExistsAsync(root + "/not-sent", token)
                        .ConfigureAwait(true)
                );
                totals.NotSent++;
            }
            string oncePath = root + "/once";
            string persistentPath = root + "/persistent";
            string leasePath = root + "/lease";
            foreach (string path in new[] { oncePath, persistentPath })
            {
                await _fixture
                    .Client.CreateAsync(path, null, LiveZooKeeperFixture.OwnerCreate, token)
                    .ConfigureAwait(true);
            }
            await owner
                .CreateAsync(
                    leasePath,
                    null,
                    new CreateOptions(LiveZooKeeperFixture.OwnerCreate.Acl, CreateMode.Ephemeral),
                    token
                )
                .ConfigureAwait(true);
            var once = (
                await owner.GetDataAndWatchAsync(oncePath, token).ConfigureAwait(true)
            ).Watch;
            await using var onceLifetime = once.ConfigureAwait(true);
            var persistent = await owner
                .AddWatchAsync(persistentPath, PersistentWatchMode.Persistent, token)
                .ConfigureAwait(true);
            await using var persistentLifetime = persistent.ConfigureAwait(true);
            var onceEvents = new EventProbe<ZooKeeperWatchEvent>(
                once.ReadEventsAsync(token),
                token
            );
            await using var onceEventsLifetime = onceEvents.ConfigureAwait(true);
            var events = new EventProbe<ZooKeeperWatchEvent>(
                persistent.ReadEventsAsync(token),
                token
            );
            await using var eventsLifetime = events.ConfigureAwait(true);
            var nextOnce = onceEvents.MoveNextAsync();
            var nextGap = events.MoveNextAsync();
            await owner
                .UpdateEndpointsAsync([new ZooKeeperEndpoint("127.0.0.1", Port(recovery))], token)
                .ConfigureAwait(true);
            int creates = source.CreateTransmissions;
            source.ArmCreateReply();
            using (var sentCancellation = new CancellationTokenSource())
            {
                uncertain = owner.CreateAsync(
                    root + "/" + AttemptPrefix,
                    null,
                    new CreateOptions(
                        LiveZooKeeperFixture.OwnerCreate.Acl,
                        CreateMode.PersistentSequential
                    ),
                    sentCancellation.Token
                );
                await source
                    .ResponseWithheld.WaitAsync(SoakMetrics.Guard, token)
                    .ConfigureAwait(true);
                Assert.Equal(FaultProxy.Success, source.WithheldError);
                await VerifySingleCreateAsync(root, token).ConfigureAwait(true);
                await sentCancellation.CancelAsync().ConfigureAwait(true);
                var unknown = await Assert
                    .ThrowsAsync<ZooKeeperOperationCanceledException>(async () =>
                        _ = await uncertain.ConfigureAwait(true)
                    )
                    .ConfigureAwait(true);
                Assert.Equal(RequestOutcome.Unknown, unknown.Outcome);
                totals.Unknown++;
            }
            sourceDisposed = true;
            await source.DisposeAsync().ConfigureAwait(true);
            Assert.Equal(creates + 1, source.CreateTransmissions);
            await recovery.RestoreHeld.WaitAsync(SoakMetrics.Guard, token).ConfigureAwait(true);
            upstreamPorts.Add(recovery.UpstreamLocalPort);
            Assert.Equal(ZooKeeperSessionState.Restoring, owner.Session.State);
            var ready = owner.WaitForConnectionAsync(token);
            Assert.False(ready.IsCompleted);
            Assert.False(nextGap.IsCompleted);
            var notReady = await Assert
                .ThrowsAsync<ZooKeeperException>(() => owner.SyncAsync(root, token))
                .ConfigureAwait(true);
            Assert.Equal(RequestOutcome.NotSent, notReady.Outcome);
            await _fixture
                .Client.SetDataAsync(oncePath, new byte[] { 2 }, ZNodeVersion.Any, token)
                .ConfigureAwait(true);
            recovery.ReleaseRestore();
            await ready.WaitAsync(SoakMetrics.Guard, token).ConfigureAwait(true);
            Assert.Equal(session, owner.Session.SessionId);
            Assert.True(owner.Session.ConnectionGeneration > generation);
            Assert.True(await nextGap.ConfigureAwait(true));
            Assert.Equal(ZooKeeperWatchEventKind.ReconnectedWithGap, events.Current.Kind);
            Assert.True(await nextOnce.ConfigureAwait(true));
            Assert.Equal(ZooKeeperWatchEventKind.NodeDataChanged, onceEvents.Current.Kind);
            Assert.False(await onceEvents.MoveNextAsync().ConfigureAwait(true));
            await _fixture
                .Client.SetDataAsync(persistentPath, new byte[] { 3 }, ZNodeVersion.Any, token)
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
            var retained = await _fixture.Client.ExistsAsync(leasePath, token).ConfigureAwait(true);
            Assert.NotNull(retained);
            Assert.Equal(session, retained.Value.EphemeralOwner);
            await VerifySingleCreateAsync(root, token).ConfigureAwait(true);
            Assert.Equal(0, recovery.CreateTransmissions);
            Assert.Equal(1, source.ConnectionAttempts);
            Assert.Equal(1, recovery.ConnectionAttempts);
            Assert.Null(source.RunFailure);
            Assert.Null(recovery.RunFailure);
            totals.Reconnects++;
            totals.WatchEvents += 3;
            await owner.CloseAsync(token).ConfigureAwait(true);
            Assert.Equal(ZooKeeperCloseStatus.Acknowledged, owner.Session.CloseStatus);
            await _fixture.Client.SyncAsync(root, token).ConfigureAwait(true);
            Assert.Null(await _fixture.Client.ExistsAsync(leasePath, token).ConfigureAwait(true));
        }
        finally
        {
            recovery?.ReleaseRestore();
            try
            {
                if (owner is not null)
                {
                    await owner.DisposeAsync().ConfigureAwait(true);
                }
            }
            finally
            {
                try
                {
                    await ObserveAsync(uncertain).ConfigureAwait(true);
                }
                finally
                {
                    try
                    {
                        if (source is not null && !sourceDisposed)
                        {
                            await source.DisposeAsync().ConfigureAwait(true);
                        }
                    }
                    finally
                    {
                        try
                        {
                            if (recovery is not null)
                            {
                                await recovery.DisposeAsync().ConfigureAwait(true);
                            }
                        }
                        finally
                        {
                            using var cleanup = new CancellationTokenSource(CleanupGuard);
                            // The namespace owner and maintenance client can use different
                            // followers. Establish the maintenance session's view with a
                            // quorum write before taking its recursive cleanup snapshot.
                            await _fixture
                                .Client.SetDataAsync(
                                    _fixture.Root,
                                    null,
                                    ZNodeVersion.Any,
                                    cleanup.Token
                                )
                                .ConfigureAwait(true);
                            await DeleteTreeAsync(root, cleanup.Token).ConfigureAwait(true);
                            await SoakMetrics
                                .PortsClosedAsync(
                                    listenerPorts.ToArray(),
                                    upstreamPorts.ToArray(),
                                    new Uri("tcp://" + Endpoint).Port,
                                    cleanup.Token
                                )
                                .ConfigureAwait(true);
                        }
                    }
                }
            }
        }
    }

    private static async Task ObserveAsync(Task<CreateResult>? request)
    {
        if (request is null)
        {
            return;
        }
        try
        {
            await request.ConfigureAwait(true);
        }
        catch (Exception error) when (error is ZooKeeperException or OperationCanceledException) { }
    }

    private async Task VerifySingleCreateAsync(string root, CancellationToken token)
    {
        await _fixture.Client.SyncAsync(root, token).ConfigureAwait(true);
        Assert.Single(
            (await _fixture.Client.GetChildrenAsync(root, token).ConfigureAwait(true)).Children,
            static child => child.StartsWith(AttemptPrefix, StringComparison.Ordinal)
        );
    }

    private async Task DeleteTreeAsync(string path, CancellationToken token)
    {
        try
        {
            foreach (
                string child in (
                    await _fixture.Client.GetChildrenAsync(path, token).ConfigureAwait(true)
                ).Children
            )
            {
                await DeleteTreeAsync(path + "/" + child, token).ConfigureAwait(true);
            }
            await _fixture.Client.DeleteAsync(path, ZNodeVersion.Any, token).ConfigureAwait(true);
        }
        catch (ZooKeeperException error) when (error.KnownErrorCode == ZooKeeperErrorCode.NoNode)
        { }
    }

    private static async Task CrudAsync(
        ZooKeeperClient owner,
        string path,
        WorkItem job,
        SoakTotals totals,
        CancellationToken token
    )
    {
        await RequestAsync(
                () => owner.CreateAsync(path, job.Initial, LiveZooKeeperFixture.OwnerCreate, token),
                totals
            )
            .ConfigureAwait(true);
        if (job.ReadBeforeWrite)
        {
            Assert.Equal(
                job.Initial,
                (
                    await RequestAsync(() => owner.GetDataAsync(path, token), totals)
                        .ConfigureAwait(true)
                ).Data!.Value.ToArray()
            );
        }
        await RequestAsync(
                () => owner.SetDataAsync(path, job.Updated, ZNodeVersion.Any, token),
                totals
            )
            .ConfigureAwait(true);
        Assert.Equal(
            job.Updated,
            (
                await RequestAsync(() => owner.GetDataAsync(path, token), totals)
                    .ConfigureAwait(true)
            ).Data!.Value.ToArray()
        );
        Interlocked.Increment(ref totals.CrudStarted);
        await owner.DeleteAsync(path, ZNodeVersion.Any, token).ConfigureAwait(true);
        Interlocked.Increment(ref totals.CrudSucceeded);
    }

    private static async Task<T> RequestAsync<T>(Func<Task<T>> operation, SoakTotals totals)
    {
        Interlocked.Increment(ref totals.CrudStarted);
        var result = await operation().ConfigureAwait(true);
        Interlocked.Increment(ref totals.CrudSucceeded);
        return result;
    }

    private static int Port(FaultProxy proxy) => new Uri("tcp://" + proxy.Endpoint).Port;
}
