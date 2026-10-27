using System.Collections.Concurrent;
using System.Diagnostics;

namespace ZkLink.IntegrationTests;

[Collection("Security")]
[Trait("Fixture", "SecurityConcurrency")]
public sealed class SecurityConcurrencyTests : IAsyncLifetime
{
    private const int TestTimeoutMilliseconds = 90_000;

    private enum Rejection
    {
        UntrustedServer,
        WrongName,
        ExpiredServer,
        MissingClient,
        UntrustedClient,
        ExpiredClient,
        Plaintext,
    }

    private const int ActorCount = 7;
    private const int RoundCount = 3;
    private static readonly TimeSpan ControlInterval = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan RoundGuard = TimeSpan.FromSeconds(16);
    private readonly SecurityFixture _control = new();
    private readonly ConcurrentDictionary<int, string> _steps = new();

    public async ValueTask InitializeAsync()
    {
        await _control.InitializeAsync().ConfigureAwait(true);
        Assert.True(_control.Mutual);
    }

    public ValueTask DisposeAsync() => _control.DisposeAsync();

    [Fact(Timeout = TestTimeoutMilliseconds)]
    [Trait("Acceptance", "T20")]
    [Trait("Acceptance", "T33")]
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Reliability",
        "CA2025",
        Justification = "Control work is awaited through WaitAsync on success; all control, readiness and actor Tasks are cancelled and observed through WhenAll before the token source is disposed on failure."
    )]
    public async Task ConcurrentRejectionsTerminateWhileValidControlRemainsUsable()
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken
        );
        for (int round = 0; round < RoundCount; round++)
        {
            var start = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously
            );
            var ready = Enumerable
                .Range(0, ActorCount)
                .Select(static _ => new TaskCompletionSource(
                    TaskCreationOptions.RunContinuationsAsynchronously
                ))
                .ToArray();
            var actors = Enumerable
                .Range(0, ActorCount)
                .Select(index =>
                    RejectAsync(
                        round * ActorCount + index,
                        (Rejection)index,
                        ready[index],
                        start.Task,
                        stop.Token
                    )
                )
                .ToArray();
            var all = Task.WhenAll(actors);
            var prepared = Task.WhenAll(ready.Select(static signal => signal.Task));
            Task controls = Task.CompletedTask;
            try
            {
                await prepared.WaitAsync(RoundGuard, stop.Token).ConfigureAwait(true);
                start.SetResult();
                controls = CheckControlWhileAsync(all, stop.Token);
                await controls.WaitAsync(RoundGuard, stop.Token).ConfigureAwait(true);
            }
            catch
            {
                foreach (var step in _steps.OrderBy(static pair => pair.Key))
                    TestContext.Current.TestOutputHelper?.WriteLine(
                        $"Actor {step.Key}: {step.Value}"
                    );
                start.TrySetCanceled(stop.Token);
                await stop.CancelAsync().ConfigureAwait(true);
                // Observe all control and actor work; the test's outer guard bounds pathological platform work.
                try
                {
                    await Task.WhenAll(all, prepared, controls).ConfigureAwait(true);
                }
                catch (Exception error)
                    when (error
                            is OperationCanceledException
                                or ZooKeeperConnectionException
                                or Xunit.Sdk.XunitException
                    ) { }
                throw;
            }
            await _control.Client.SyncAsync(_control.Root, stop.Token).ConfigureAwait(true);
            Assert.Equal(ZooKeeperSessionState.Connected, _control.Client.Session.State);
        }
    }

    private async Task CheckControlWhileAsync(Task actors, CancellationToken token)
    {
        while (!actors.IsCompleted)
        {
            await _control.Client.SyncAsync(_control.Root, token).ConfigureAwait(false);
            await Task.Delay(ControlInterval, token).ConfigureAwait(false);
        }
        await actors.ConfigureAwait(false);
    }

    private async Task RejectAsync(
        int id,
        Rejection kind,
        TaskCompletionSource ready,
        Task start,
        CancellationToken token
    )
    {
        var clock = Stopwatch.StartNew();
        void Step(string value)
        {
            string state = $"{value} at {clock.ElapsedMilliseconds}ms";
            _steps[id] = state;
            TestContext.Current.TestOutputHelper?.WriteLine($"Actor {id}: {state}");
        }
        var fixture = new SecurityFixture(Step);
        try
        {
            Step("initialize");
            await fixture.InitializeAsync().ConfigureAwait(false);
            ready.SetResult();
            await start.WaitAsync(token).ConfigureAwait(false);
            var tls = kind switch
            {
                Rejection.UntrustedServer => fixture.Tls(
                    fixture.Certificate,
                    root: fixture.UntrustedRoot
                ),
                Rejection.WrongName => fixture.Tls(
                    fixture.Certificate,
                    serverName: "wrong.zklink.test"
                ),
                Rejection.ExpiredServer => fixture.Tls(fixture.Certificate),
                Rejection.MissingClient => fixture.Tls(),
                Rejection.UntrustedClient => fixture.Tls(fixture.UntrustedCertificate),
                Rejection.ExpiredClient => fixture.Tls(fixture.ExpiredCertificate),
                Rejection.Plaintext => fixture.Tls(fixture.Certificate),
                _ => throw new ArgumentOutOfRangeException(nameof(kind)),
            };
            string endpoint = kind switch
            {
                Rejection.ExpiredServer => fixture.ExpiredEndpoint,
                Rejection.Plaintext => fixture.PlaintextEndpoint,
                _ => fixture.ConnectionString,
            };
            Step("reject.begin");
            var error = await Assert
                .ThrowsAsync<ZooKeeperConnectionException>(async () =>
                {
                    // Original four-second initial connection budget, not the isolated probe budget.
                    var unexpected = await fixture
                        .ConnectWithCancellationAsync(
                            endpoint,
                            digest: true,
                            tls,
                            rejectionProbe: false,
                            token
                        )
                        .ConfigureAwait(false);
                    await using var lifetime = unexpected.ConfigureAwait(false);
                })
                .ConfigureAwait(false);
            Step("reject.end");
            fixture.AssertSafe(error);
            if (
                kind
                is Rejection.UntrustedServer
                    or Rejection.WrongName
                    or Rejection.ExpiredServer
                    or Rejection.Plaintext
            )
                Assert.Equal(ZooKeeperConnectionFailure.Tls, error.Failure);
            else
                Assert.True(
                    error.Failure
                        is ZooKeeperConnectionFailure.Tls
                            or ZooKeeperConnectionFailure.Timeout
                            or ZooKeeperConnectionFailure.Unreachable
                );
            await fixture.Client.SyncAsync(fixture.Root, token).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            ready.TrySetException(error);
            throw;
        }
        finally
        {
            Step("dispose.begin");
            await fixture.DisposeAsync().ConfigureAwait(false);
            Step("done");
        }
    }
}
