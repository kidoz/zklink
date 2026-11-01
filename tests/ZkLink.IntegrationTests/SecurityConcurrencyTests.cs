using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.NetworkInformation;
using ZkLink.Testing;

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
    private static readonly TimeSpan CleanupPoll = TimeSpan.FromMilliseconds(25);
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
        using var trace = new SecurityHandshakeTrace(line =>
            TestContext.Current.TestOutputHelper?.WriteLine(line)
        );
        long controlSession = _control.Client.Session.SessionId;
        long controlGeneration = _control.Client.Session.ConnectionGeneration;
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
                        trace,
                        stop.Token
                    )
                )
                .ToArray();
            var fresh = OpenFreshControlAsync(
                ActorCount * RoundCount + round,
                start.Task,
                trace,
                stop.Token
            );
            var all = Task.WhenAll(actors.Append(fresh));
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
                {
                    TestContext.Current.TestOutputHelper?.WriteLine(
                        $"Actor {step.Key}: {step.Value}"
                    );
                }
                ReportTrace(trace);
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
            Assert.Equal(controlSession, _control.Client.Session.SessionId);
            Assert.Equal(controlGeneration, _control.Client.Session.ConnectionGeneration);
            await AssertOwnedConnectionsAsync(stop.Token).ConfigureAwait(true);
            var snapshot = trace.Snapshot();
            Assert.Equal(0, snapshot.Dropped);
            Assert.Equal(0, snapshot.OutputFailures);
            foreach (int id in Enumerable.Range(round * ActorCount, ActorCount))
            {
                AssertCompleteHandshakes(snapshot, id, SecurityProbeUse.Setup);
                AssertCompleteHandshakes(snapshot, id, SecurityProbeUse.Rejection);
            }
            AssertCompleteHandshakes(
                snapshot,
                ActorCount * RoundCount + round,
                SecurityProbeUse.FreshControl
            );
        }
        ReportTrace(trace);
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
        SecurityHandshakeTrace trace,
        CancellationToken token
    )
    {
        using var setup = trace.EnterProbe(id, SecurityProbeUse.Setup);
        var clock = Stopwatch.StartNew();
        void Step(string value)
        {
            string state = FormattableString.Invariant(
                $"rejection={kind} event={value} elapsed_ms={clock.Elapsed.TotalMilliseconds:F4} observed_at_utc={DateTimeOffset.UtcNow:O}"
            );
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
            long session = fixture.Client.Session.SessionId;
            long generation = fixture.Client.Session.ConnectionGeneration;
            ZooKeeperConnectionException error;
            using (trace.EnterProbe(id, SecurityProbeUse.Rejection))
            {
                error = await Assert
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
            }
            Step("reject.result." + error.Failure);
            Step("reject.end");
            fixture.AssertSafe(error);
            if (
                kind
                is Rejection.UntrustedServer
                    or Rejection.WrongName
                    or Rejection.ExpiredServer
                    or Rejection.Plaintext
            )
            {
                Assert.Equal(ZooKeeperConnectionFailure.Tls, error.Failure);
            }
            else
            {
                Assert.True(
                    error.Failure
                        is ZooKeeperConnectionFailure.Tls
                            or ZooKeeperConnectionFailure.Timeout
                            or ZooKeeperConnectionFailure.Unreachable
                );
            }
            await fixture.Client.SyncAsync(fixture.Root, token).ConfigureAwait(false);
            Assert.Equal(session, fixture.Client.Session.SessionId);
            Assert.Equal(generation, fixture.Client.Session.ConnectionGeneration);
            Assert.Equal(ZooKeeperSessionState.Connected, fixture.Client.Session.State);
            Step("control.usable");
        }
        catch (Exception error)
        {
            Step("primary." + error.GetType().Name);
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

    private async Task OpenFreshControlAsync(
        int id,
        Task start,
        SecurityHandshakeTrace trace,
        CancellationToken token
    )
    {
        await start.WaitAsync(token).ConfigureAwait(false);
        using var scope = trace.EnterProbe(id, SecurityProbeUse.FreshControl);
        TestContext.Current.TestOutputHelper?.WriteLine($"Fresh control {id}: connect.begin");
        var client = await _control
            .ConnectWithCancellationAsync(null, true, null, false, token)
            .ConfigureAwait(false);
        await using (client.ConfigureAwait(false))
        {
            await client.SyncAsync(_control.Root, token).ConfigureAwait(false);
            await client.CloseAsync(token).ConfigureAwait(false);
            Assert.Equal(ZooKeeperCloseStatus.Acknowledged, client.Session.CloseStatus);
        }
        TestContext.Current.TestOutputHelper?.WriteLine($"Fresh control {id}: acknowledged-close");
    }

    private static void AssertCompleteHandshakes(
        SecurityHandshakeSnapshot snapshot,
        int id,
        SecurityProbeUse use
    )
    {
        var events = snapshot.Events.Where(item => item.Actor == id && item.Use == use).ToArray();
        int started = events.Count(item => item.Event == SecurityHandshakeEvent.Start);
        int stopped = events.Count(item => item.Event == SecurityHandshakeEvent.Stop);
        // HandshakeFailed is followed by HandshakeStop, so it is not another
        // completion. Do not double-count a failed TLS handshake as two stops.
        Assert.True(started > 0, $"No TLS start observation for actor {id} use {use}.");
        Assert.Equal(started, stopped);
    }

    private static void ReportTrace(SecurityHandshakeTrace trace)
    {
        var snapshot = trace.Snapshot();
        TestContext.Current.TestOutputHelper?.WriteLine(
            $"TLS trace: retained={snapshot.Events.Count} dropped={snapshot.Dropped} unattributed={snapshot.Unattributed} output-failures={snapshot.OutputFailures}"
        );
    }

    private async Task AssertOwnedConnectionsAsync(CancellationToken token)
    {
        const int ControlConnections = 1;
        var ports = (
            _control.ConnectionString
            + ","
            + _control.PlaintextEndpoint
            + ","
            + _control.ExpiredEndpoint
        )
            .Split(',')
            .Select(endpoint => new Uri("tcp://" + endpoint).Port)
            .ToHashSet();
        var clock = Stopwatch.StartNew();
        int open;
        do
        {
            open = IPGlobalProperties
                .GetIPGlobalProperties()
                .GetActiveTcpConnections()
                .Count(connection =>
                    ports.Contains(connection.RemoteEndPoint.Port)
                    && connection.State
                        is not (TcpState.TimeWait or TcpState.Closed or TcpState.DeleteTcb)
                );
            if (open == ControlConnections)
            {
                TestContext.Current.TestOutputHelper?.WriteLine(
                    $"TLS resources: active-client-tcp={open} expected-control={ControlConnections}"
                );
                return;
            }
            await Task.Delay(CleanupPoll, token).ConfigureAwait(false);
        } while (clock.Elapsed < RoundGuard);
        Assert.Equal(ControlConnections, open);
    }
}
