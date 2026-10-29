using System.Diagnostics;
using ZkLink.IntegrationTests;

namespace ZkLink.FaultTests;

[Collection("WatchPressure")]
[Trait("Fixture", "Soak")]
public sealed class SoakDeadlineTests : IAsyncLifetime
{
    private const int TestGuardMilliseconds = 60_000;
    private static readonly TimeSpan OperationDeadline = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ObservationGuard = TimeSpan.FromSeconds(10);
    private readonly LiveZooKeeperFixture _fixture = new();

    public ValueTask InitializeAsync() => _fixture.InitializeAsync();

    public ValueTask DisposeAsync() => _fixture.DisposeAsync();

    [Fact(Timeout = TestGuardMilliseconds)]
    [Trait("Acceptance", "T14")]
    [Trait("Acceptance", "T33")]
    public async Task DelayedSyncTimesOutWithoutReplayAndDrainsItsLateReply()
    {
        var token = TestContext.Current.CancellationToken;
        string endpoint =
            Environment.GetEnvironmentVariable("ZKLINK_FAULT_ENDPOINT")
            ?? throw new InvalidOperationException("The owned soak forwarding target is required.");
        var proxy = new FaultProxy(endpoint, holdRestore: false, token);
        using var metrics = new SoakMetrics();
        int listenerPort = new Uri("tcp://" + proxy.Endpoint).Port;
        int targetPort = new Uri("tcp://" + endpoint).Port;
        int upstreamPort;
        await using (proxy.ConfigureAwait(true))
        {
            var owner = await _fixture
                .ConnectAsync(connectionString: proxy.Endpoint, operationTimeout: OperationDeadline)
                .ConfigureAwait(true);
            await using (owner.ConfigureAwait(true))
            {
                upstreamPort = proxy.UpstreamLocalPort;
                await owner.SyncAsync(_fixture.Root, token).ConfigureAwait(true);
                long session = owner.Session.SessionId;
                long generation = owner.Session.ConnectionGeneration;
                int before = proxy.SyncTransmissions;
                proxy.ArmSyncReply();
                var elapsed = Stopwatch.StartNew();
                Task delayed = owner.SyncAsync(_fixture.Root, token);
                try
                {
                    await proxy
                        .SyncReplyHeld.WaitAsync(ObservationGuard, token)
                        .ConfigureAwait(true);
                    Assert.Equal(FaultProxy.Success, proxy.SyncReplyError);
                    // The real server has answered; retain the frame until the unchanged
                    // application deadline fires. This isolates late-reply handling from disk I/O.
                    var error = await Assert
                        .ThrowsAsync<ZooKeeperTimeoutException>(() =>
                            delayed.WaitAsync(ObservationGuard, token)
                        )
                        .ConfigureAwait(true);
                    Assert.Equal(RequestOutcome.Unknown, error.Outcome);
                    Assert.Equal(ZooKeeperOperation.Sync, error.Request.Operation);
                    Assert.True(elapsed.Elapsed >= OperationDeadline);
                    Assert.Equal(before + 1, proxy.SyncTransmissions);
                    Assert.Equal(ZooKeeperSessionState.Connected, owner.Session.State);
                    proxy.ReleaseSyncReply();
                    await owner.SyncAsync(_fixture.Root, token).ConfigureAwait(true);
                    Assert.Equal(before + 2, proxy.SyncTransmissions);
                    Assert.Equal(session, owner.Session.SessionId);
                    Assert.Equal(generation, owner.Session.ConnectionGeneration);
                    Assert.Equal(1, proxy.ConnectionAttempts);
                    Assert.Null(proxy.RunFailure);
                    await owner.CloseAsync(token).ConfigureAwait(true);
                    Assert.Equal(ZooKeeperCloseStatus.Acknowledged, owner.Session.CloseStatus);
                    TestContext.Current.TestOutputHelper?.WriteLine(
                        $"sync-deadline-recovery: PASS elapsed-ms={elapsed.ElapsedMilliseconds}"
                    );
                }
                finally
                {
                    proxy.ReleaseSyncReply();
                    try
                    {
                        await delayed.ConfigureAwait(true);
                    }
                    catch (ZooKeeperTimeoutException) { }
                }
            }
        }
        await metrics.QuiescentAsync(token).ConfigureAwait(true);
        await SoakMetrics
            .PortsClosedAsync([listenerPort], [upstreamPort], targetPort, token)
            .ConfigureAwait(true);
    }
}
