using System.Diagnostics;
using ZkLink.IntegrationTests;

namespace ZkLink.FaultTests;

[Collection("WatchPressure")]
[Trait("Fixture", "Soak")]
public sealed class SoakDeadlineTests : IAsyncLifetime
{
    private const int TestGuardMilliseconds = 60_000;
    private static readonly TimeSpan OperationDeadline = TimeSpan.FromSeconds(5);

    // System timers truncate fractional milliseconds and use the platform clock.
    // The controlled-peer check asserts the exact boundary without this allowance.
    private static readonly TimeSpan SystemTimerResolutionAllowance = TimeSpan.FromMilliseconds(20);
    private static readonly TimeSpan ObservationGuard = TimeSpan.FromSeconds(10);
    private readonly LiveZooKeeperFixture _fixture = new();

    public ValueTask InitializeAsync() => _fixture.InitializeAsync();

    public ValueTask DisposeAsync() => _fixture.DisposeAsync();

    [Theory(Timeout = TestGuardMilliseconds)]
    [InlineData(ZooKeeperOperation.Sync)]
    [InlineData(ZooKeeperOperation.Delete)]
    [Trait("Acceptance", "T14")]
    [Trait("Acceptance", "T33")]
    public async Task DelayedEmptyReplyTimesOutWithoutReplayAndDrainsItsLateReply(
        ZooKeeperOperation operation
    )
    {
        var token = TestContext.Current.CancellationToken;
        string endpoint =
            Environment.GetEnvironmentVariable("ZKLINK_FAULT_ENDPOINT")
            ?? throw new InvalidOperationException("The owned soak forwarding target is required.");
        var sockets = new SoakSocketScope();
        var proxy = new FaultProxy(endpoint, holdRestore: false, token, sockets);
        using var metrics = new SoakMetrics();
        await using (proxy.ConfigureAwait(true))
        {
            var owner = await _fixture
                .ConnectAsync(connectionString: proxy.Endpoint, operationTimeout: OperationDeadline)
                .ConfigureAwait(true);
            await using (owner.ConfigureAwait(true))
            {
                await owner.SyncAsync(_fixture.Root, token).ConfigureAwait(true);
                long session = owner.Session.SessionId;
                long generation = owner.Session.ConnectionGeneration;
                string path =
                    operation == ZooKeeperOperation.Delete
                        ? _fixture.Root + "/delayed-delete"
                        : _fixture.Root;
                if (operation == ZooKeeperOperation.Delete)
                {
                    await owner
                        .CreateAsync(path, null, LiveZooKeeperFixture.OwnerCreate, token)
                        .ConfigureAwait(true);
                    proxy.ArmDeleteReply();
                }
                else
                {
                    proxy.ArmSyncReply();
                }
                int before =
                    operation == ZooKeeperOperation.Delete
                        ? proxy.DeleteTransmissions
                        : proxy.SyncTransmissions;
                var elapsed = Stopwatch.StartNew();
                Task delayed =
                    operation == ZooKeeperOperation.Delete
                        ? owner.DeleteAsync(path, ZNodeVersion.Any, token)
                        : owner.SyncAsync(path, token);
                try
                {
                    await proxy
                        .WaitForSignalAsync(
                            operation == ZooKeeperOperation.Delete
                                ? proxy.DeleteReplyHeld
                                : proxy.SyncReplyHeld,
                            ObservationGuard,
                            token
                        )
                        .ConfigureAwait(true);
                    Assert.Equal(
                        FaultProxy.Success,
                        operation == ZooKeeperOperation.Delete
                            ? proxy.DeleteReplyError
                            : proxy.SyncReplyError
                    );
                    if (operation == ZooKeeperOperation.Delete)
                    {
                        // A write on the maintenance leader fences the independent read.
                        await _fixture
                            .Client.SetDataAsync(
                                _fixture.Root,
                                new byte[] { 1 },
                                ZNodeVersion.Any,
                                token
                            )
                            .ConfigureAwait(true);
                        Assert.Null(
                            await _fixture.Client.ExistsAsync(path, token).ConfigureAwait(true)
                        );
                    }
                    // The real server has answered; retain the frame until the unchanged
                    // application deadline fires. This isolates late-reply handling from disk I/O.
                    var error = await Assert
                        .ThrowsAsync<ZooKeeperTimeoutException>(() =>
                            delayed.WaitAsync(ObservationGuard, token)
                        )
                        .ConfigureAwait(true);
                    Assert.Equal(RequestOutcome.Unknown, error.Outcome);
                    Assert.Equal(operation, error.Request.Operation);
                    Assert.Equal(path, error.Request.Path);
                    if (operation == ZooKeeperOperation.Delete)
                    {
                        Assert.Equal(proxy.DeleteReplyXid, error.Request.RequestId);
                    }
                    Assert.True(
                        elapsed.Elapsed + SystemTimerResolutionAllowance >= OperationDeadline,
                        $"Deadline elapsed-ms={elapsed.Elapsed.TotalMilliseconds:F4}; configured-ms={OperationDeadline.TotalMilliseconds:F0}; system-timer-allowance-ms={SystemTimerResolutionAllowance.TotalMilliseconds:F0}."
                    );
                    Assert.Equal(
                        before + 1,
                        operation == ZooKeeperOperation.Delete
                            ? proxy.DeleteTransmissions
                            : proxy.SyncTransmissions
                    );
                    Assert.Equal(ZooKeeperSessionState.Connected, owner.Session.State);
                    proxy.ReleaseSyncReply();
                    proxy.ReleaseDeleteReply();
                    await owner.SyncAsync(_fixture.Root, token).ConfigureAwait(true);
                    if (operation == ZooKeeperOperation.Delete)
                    {
                        Assert.Equal(before + 1, proxy.DeleteTransmissions);
                        Assert.Null(await owner.ExistsAsync(path, token).ConfigureAwait(true));
                    }
                    else
                    {
                        Assert.Equal(before + 2, proxy.SyncTransmissions);
                    }
                    Assert.Equal(session, owner.Session.SessionId);
                    Assert.Equal(generation, owner.Session.ConnectionGeneration);
                    Assert.Equal(1, proxy.ConnectionAttempts);
                    Assert.Null(proxy.RunFailure);
                    await owner.CloseAsync(token).ConfigureAwait(true);
                    Assert.Equal(ZooKeeperCloseStatus.Acknowledged, owner.Session.CloseStatus);
                    TestContext.Current.TestOutputHelper?.WriteLine(
                        $"{operation}-deadline-recovery: PASS elapsed-ms={elapsed.ElapsedMilliseconds} outcome=Unknown same-generation=true no-replay=true"
                    );
                }
                finally
                {
                    proxy.ReleaseDeleteReply();
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
        await SoakMetrics.PortsClosedAsync(sockets, token).ConfigureAwait(true);
    }
}
