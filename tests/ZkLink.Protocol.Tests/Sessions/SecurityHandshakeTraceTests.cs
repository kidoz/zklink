using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using ZkLink.Protocol.Tests.Peers;
using ZkLink.Testing;

namespace ZkLink.Protocol.Tests.Sessions;

public sealed class SecurityHandshakeTraceTests
{
    private const int TestTimeoutMilliseconds = 15_000;
    private const string SecretHost = "trace-secret.zk.test";
    private static readonly TimeSpan ObservationGuard = TimeSpan.FromSeconds(5);

    [Fact(Timeout = TestTimeoutMilliseconds)]
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Reliability",
        "CA2025",
        Justification = "Task.WhenAll observes both handshakes, including either failure, before the shared trace and certificates leave scope."
    )]
    public async Task ConcurrentRealHandshakesRetainActorIdentityAndExcludePayloads()
    {
        var token = TestContext.Current.CancellationToken;
        using var certificates = new EphemeralCertificates();
        using var trace = new SecurityHandshakeTrace();
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var valid = HandshakeAsync(certificates, trace, start.Task, invalid: false, token);
        var invalid = HandshakeAsync(certificates, trace, start.Task, invalid: true, token);
        start.SetResult();
        await Task.WhenAll(valid, invalid).ConfigureAwait(true);
        var snapshot = trace.Snapshot();
        Assert.Equal(0, snapshot.Dropped);
        Assert.Equal(0, snapshot.OutputFailures);
        foreach (int actor in new[] { 0, 1 })
        {
            var events = snapshot.Events.Where(item => item.Actor == actor).ToArray();
            Assert.Equal(1, events.Count(item => item.Event == SecurityHandshakeEvent.Start));
            Assert.Equal(1, events.Count(item => item.Event == SecurityHandshakeEvent.Stop));
            Assert.Equal(actor, events.Count(item => item.Event == SecurityHandshakeEvent.Failed));
            Assert.All(events, item => Assert.Equal(SecurityProbeUse.Rejection, item.Use));
        }
        Assert.All(
            snapshot.Events,
            item =>
            {
                Assert.DoesNotContain(SecretHost, item.ToJson(), StringComparison.Ordinal);
                Assert.DoesNotContain(
                    "certificate",
                    item.ToJson(),
                    StringComparison.OrdinalIgnoreCase
                );
                Assert.DoesNotContain("PRIVATE KEY", item.ToJson(), StringComparison.Ordinal);
            }
        );
    }

    [Fact(Timeout = TestTimeoutMilliseconds)]
    public async Task StalledHandshakeIsVisibleBeforeCancellationAndEndsExactlyOnce()
    {
        var token = TestContext.Current.CancellationToken;
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var trace = new SecurityHandshakeTrace(line =>
        {
            if (line.Contains("\"event\":\"Start\"", StringComparison.Ordinal))
            {
                started.TrySetResult();
            }
        });
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var client = new TcpClient();
        await client
            .ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port, token)
            .ConfigureAwait(true);
        using var peer = await listener.AcceptTcpClientAsync(token).ConfigureAwait(true);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        using var scope = trace.EnterProbe(0, SecurityProbeUse.Rejection);
        var tls = new SslStream(client.GetStream());
        await using var tlsLifetime = tls.ConfigureAwait(true);
        var handshake = tls.AuthenticateAsClientAsync(
            new SslClientAuthenticationOptions { TargetHost = SecretHost },
            stop.Token
        );
        await started.Task.WaitAsync(ObservationGuard, token).ConfigureAwait(true);
        var pending = trace.Snapshot();
        Assert.Single(pending.Events, item => item.Event == SecurityHandshakeEvent.Start);
        Assert.DoesNotContain(pending.Events, item => item.Event == SecurityHandshakeEvent.Stop);
        Assert.False(handshake.IsCompleted);
        await stop.CancelAsync().ConfigureAwait(true);
        await Assert
            .ThrowsAnyAsync<OperationCanceledException>(() => handshake)
            .ConfigureAwait(true);
        var ended = trace.Snapshot();
        Assert.Single(ended.Events, item => item.Event == SecurityHandshakeEvent.Failed);
        Assert.Single(ended.Events, item => item.Event == SecurityHandshakeEvent.Stop);
        Assert.Equal(0, ended.Dropped);
        Assert.All(
            ended.Events,
            item => Assert.DoesNotContain(SecretHost, item.ToJson(), StringComparison.Ordinal)
        );
    }

    [Fact(Timeout = TestTimeoutMilliseconds)]
    public async Task TraceRetainsItsBoundAndReportsOverflowInsteadOfGrowing()
    {
        using var certificates = new EphemeralCertificates();
        using var trace = new SecurityHandshakeTrace(capacity: 1);
        await HandshakeAsync(
                certificates,
                trace,
                Task.CompletedTask,
                invalid: false,
                TestContext.Current.CancellationToken
            )
            .ConfigureAwait(true);
        var snapshot = trace.Snapshot();
        Assert.Single(snapshot.Events);
        Assert.Equal(1, snapshot.Dropped);
        Assert.Equal(SecurityHandshakeEvent.Start, snapshot.Events[0].Event);
    }

    [Fact(Timeout = TestTimeoutMilliseconds)]
    public async Task OutputFailureCannotChangeTheTlsResultAndRemainsObservable()
    {
        using var certificates = new EphemeralCertificates();
        using var trace = new SecurityHandshakeTrace(_ =>
            throw new InvalidOperationException("test output unavailable")
        );
        await HandshakeAsync(
                certificates,
                trace,
                Task.CompletedTask,
                invalid: false,
                TestContext.Current.CancellationToken
            )
            .ConfigureAwait(true);
        var snapshot = trace.Snapshot();
        Assert.Equal(2, snapshot.OutputFailures);
        Assert.Equal(2, snapshot.Events.Count);
        Assert.Equal(0, snapshot.Dropped);
    }

    private static async Task HandshakeAsync(
        EphemeralCertificates certificates,
        SecurityHandshakeTrace trace,
        Task start,
        bool invalid,
        CancellationToken token
    )
    {
        var server = new FakeZooKeeperServer(
            async (stream, serverToken) =>
            {
                var tls = new SslStream(stream, leaveInnerStreamOpen: true);
                await using var tlsLifetime = tls.ConfigureAwait(false);
                try
                {
                    await tls.AuthenticateAsServerAsync(
                            certificates.ServerOptions(mutual: false),
                            serverToken
                        )
                        .ConfigureAwait(false);
                    await tls.ReadAsync(new byte[1], serverToken).ConfigureAwait(false);
                }
                catch (AuthenticationException) { }
                catch (IOException) { }
            },
            token
        );
        await using var serverLifetime = server.ConfigureAwait(false);
        await start.WaitAsync(token).ConfigureAwait(false);
        using var scope = trace.EnterProbe(invalid ? 1 : 0, SecurityProbeUse.Rejection);
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, server.Port, token).ConfigureAwait(false);
        var policy = new X509ChainPolicy
        {
            TrustMode = X509ChainTrustMode.CustomRootTrust,
            RevocationMode = X509RevocationMode.NoCheck,
        };
        policy.CustomTrustStore.Add(certificates.Root);
        var clientTls = new SslStream(client.GetStream());
        await using (clientTls.ConfigureAwait(false))
        {
            var authentication = clientTls.AuthenticateAsClientAsync(
                new SslClientAuthenticationOptions
                {
                    TargetHost = invalid ? SecretHost : "zk.test",
                    CertificateChainPolicy = policy,
                },
                token
            );
            if (invalid)
            {
                await Assert
                    .ThrowsAsync<AuthenticationException>(() => authentication)
                    .ConfigureAwait(false);
            }
            else
            {
                await authentication.ConfigureAwait(false);
            }
        }
        await server.Completion.WaitAsync(ObservationGuard, token).ConfigureAwait(false);
    }
}
