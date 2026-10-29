using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using ZkLink.Configuration;
using ZkLink.Protocol.Tests.Peers;
using ZkLink.Sessions;
using ZkLink.Transport;

namespace ZkLink.Protocol.Tests.Sessions;

public sealed class RevocationTests
{
    private const int TestDeadlineMilliseconds = 30000;
    private const int RandomSeed = 1729;
    private static readonly TimeSpan ConnectGuard = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan AttemptDeadline = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan DeadlineCleanupGuard = TimeSpan.FromSeconds(2);
    private const int RevocationEndpointsPerCertificate = 2;
    private static readonly TimeSpan PlatformCleanupGuard =
        AttemptDeadline * RevocationEndpointsPerCertificate + DeadlineCleanupGuard;

    [Fact(Timeout = TestDeadlineMilliseconds)]
    [Trait("Acceptance", "T20")]
    public async Task DefaultOnlinePolicyFetchesRevocationStatusAndCompletesSession()
    {
        var authority = new RevocationAuthority();
        await using var authorityLifetime = authority.ConfigureAwait(true);
        await CompleteSessionAsync(authority, TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        Assert.True(authority.Requests > 0);
    }

    [Theory(Timeout = TestDeadlineMilliseconds)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [Trait("Acceptance", "T20")]
    public async Task ExplicitNoCheckControlIsolatesRevocationFromTrustAndName(
        bool revoked,
        bool unavailable
    )
    {
        var authority = new RevocationAuthority(revoked, unavailable);
        await using var authorityLifetime = authority.ConfigureAwait(true);
        await CompleteSessionAsync(authority, TestContext.Current.CancellationToken, noCheck: true)
            .ConfigureAwait(true);
        Assert.Equal(0, authority.Requests);
    }

    private static async Task CompleteSessionAsync(
        RevocationAuthority authority,
        CancellationToken token,
        bool noCheck = false
    )
    {
        var server = new FakeZooKeeperServer(
            async (stream, token) =>
            {
                using var tls = new SslStream(stream, leaveInnerStreamOpen: true);
                await tls.AuthenticateAsServerAsync(
                        new SslServerAuthenticationOptions { ServerCertificate = authority.Server },
                        token
                    )
                    .ConfigureAwait(false);
                await TransportSecurityTests.ServeSessionAsync(tls, token).ConfigureAwait(false);
            },
            token
        );
        await using var serverLifetime = server.ConfigureAwait(true);
        var settings = Settings(authority, server.Port);
        Assert.Equal(X509RevocationMode.Online, settings.Tls.RevocationMode);
        if (noCheck)
        {
            settings = Settings(
                authority,
                server.Port,
                tls: ZooKeeperTlsOptions.Required(
                    X509RevocationMode.NoCheck,
                    trustRoots: [authority.Root],
                    serverName: authority.ServerName
                )
            );
        }
        var client = await ZooKeeperClient.ConnectAsync(settings, token).ConfigureAwait(true);
        await using var clientLifetime = client.ConfigureAwait(true);
        await client.SyncAsync("/", token).ConfigureAwait(true);
        await client.CloseAsync(token).ConfigureAwait(true);
        await server.Completion.ConfigureAwait(true);
        Assert.Equal(ZooKeeperCloseStatus.Acknowledged, client.Session.CloseStatus);
    }

    [Theory(Timeout = TestDeadlineMilliseconds)]
    [InlineData(true)]
    [InlineData(false)]
    [Trait("Acceptance", "T20")]
    [Trait("Acceptance", "T14")]
    public async Task HeldRevocationResponseRespectsCancellationAndInitialDeadline(
        bool callerCancellation
    )
    {
        var authority = new RevocationAuthority(holdResponse: true);
        await using var authorityLifetime = authority.ConfigureAwait(true);
        int applicationBytes = 0;
        var server = RejectionServer(authority, value => applicationBytes = value);
        await using var serverLifetime = server.ConfigureAwait(true);
        var settings = Settings(authority, server.Port, AttemptDeadline);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken
        );
        var clock = new ManualTimeProvider();
        Task<ZooKeeperClient> connection = ConnectWithClock(settings, clock, cancellation.Token);
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            await authority
                .Requested.WaitAsync(ConnectGuard, TestContext.Current.CancellationToken)
                .ConfigureAwait(true);
            if (callerCancellation)
            {
                await cancellation.CancelAsync().ConfigureAwait(true);
                var error = await Assert
                    .ThrowsAnyAsync<OperationCanceledException>(async () =>
                        await connection
                            .WaitAsync(PlatformCleanupGuard, TestContext.Current.CancellationToken)
                            .ConfigureAwait(true)
                    )
                    .ConfigureAwait(true);
                Assert.Equal(cancellation.Token, error.CancellationToken);
            }
            else
            {
                // Expire the driver budget while the native status request is held.
                clock.Advance(AttemptDeadline);
                var error = await Assert
                    .ThrowsAsync<ZooKeeperConnectionException>(async () =>
                        await connection
                            .WaitAsync(PlatformCleanupGuard, TestContext.Current.CancellationToken)
                            .ConfigureAwait(true)
                    )
                    .ConfigureAwait(true);
                Assert.Equal(ZooKeeperConnectionFailure.Timeout, error.Failure);
            }
        }
        finally
        {
            TestContext.Current.TestOutputHelper?.WriteLine(
                $"Held retrieval: completed={connection.IsCompleted}, elapsed-ms={elapsed.ElapsedMilliseconds}"
            );
            authority.Release();
            try
            {
                var unexpected = await connection.ConfigureAwait(true);
                await unexpected.DisposeAsync().ConfigureAwait(true);
            }
            catch (ZooKeeperConnectionException) { }
            catch (OperationCanceledException) { }
        }
        await server.Completion.ConfigureAwait(true);
        Assert.Equal(0, applicationBytes);
    }

    [Theory(Timeout = TestDeadlineMilliseconds)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    [Trait("Acceptance", "T20")]
    public async Task InvalidOrUnavailableRevocationStatusRejectsBeforeZooKeeperBytes(
        bool revoked,
        bool unavailable,
        bool invalidSignature
    )
    {
        var authority = new RevocationAuthority(
            revoked,
            unavailable,
            invalidSignature: invalidSignature
        );
        await using var authorityLifetime = authority.ConfigureAwait(true);
        int applicationBytes = 0;
        var server = RejectionServer(authority, value => applicationBytes = value);
        await using var serverLifetime = server.ConfigureAwait(true);
        using var options = ClientOptionsSnapshot.Capture(Settings(authority, server.Port));
        using var guard = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken
        );
        guard.CancelAfter(ConnectGuard);
        var error = await Assert
            .ThrowsAsync<ZooKeeperConnectionException>(() =>
                Connector()
                    .ConnectAsync(
                        new ZooKeeperEndpoint("127.0.0.1", server.Port),
                        options,
                        guard.Token
                    )
            )
            .ConfigureAwait(true);
        Assert.Equal(ZooKeeperConnectionFailure.Tls, error.Failure);
        await server.Completion.ConfigureAwait(true);
        Assert.True(authority.Requests > 0);
        Assert.Equal(0, applicationBytes);
    }

    private static FakeZooKeeperServer RejectionServer(
        RevocationAuthority authority,
        Action<int> observed
    ) =>
        new(
            async (stream, token) =>
            {
                using var tls = new SslStream(stream, leaveInnerStreamOpen: true);
                try
                {
                    await tls.AuthenticateAsServerAsync(
                            new SslServerAuthenticationOptions
                            {
                                ServerCertificate = authority.Server,
                            },
                            token
                        )
                        .ConfigureAwait(false);
                    var buffer = new byte[1];
                    observed(await tls.ReadAsync(buffer, token).ConfigureAwait(false));
                }
                catch (AuthenticationException) { }
                catch (IOException) { }
            },
            TestContext.Current.CancellationToken
        );

    private static ZooKeeperClientOptions Settings(
        RevocationAuthority authority,
        int port,
        TimeSpan? guard = null,
        ZooKeeperTlsOptions? tls = null
    ) =>
        new()
        {
            ConnectionString = $"127.0.0.1:{port}",
            Tls =
                tls
                ?? ZooKeeperTlsOptions.Required(
                    trustRoots: [authority.Root],
                    serverName: authority.ServerName
                ),
            InitialConnectTimeout = guard ?? ConnectGuard,
            ConnectAttemptTimeout = guard ?? ConnectGuard,
        };

    private static Task<ZooKeeperClient> ConnectWithClock(
        ZooKeeperClientOptions settings,
        TimeProvider clock,
        CancellationToken token
    ) =>
        ZooKeeperClient.ConnectCoreAsync(
            ClientOptionsSnapshot.Capture(settings),
            clock,
            Connector(),
            new SessionRandom(new Random(RandomSeed)),
            token
        );

    private static SocketTransportConnector Connector() =>
        new(new HostResolver(), new SessionRandom(new Random(RandomSeed)));
}
