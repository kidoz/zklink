using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using NSubstitute;
using ZkLink.Configuration;
using ZkLink.Protocol;
using ZkLink.Protocol.Tests.Peers;
using ZkLink.Sessions;
using ZkLink.Transport;

namespace ZkLink.Protocol.Tests.Sessions;

public sealed class TransportSecurityTests
{
    [Theory(Timeout = 10000)]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Acceptance", "T20")]
    public async Task TrustedTlsAndMutualTlsCompleteHandshakeSyncAndClose(bool mutual)
    {
        using var certificates = new EphemeralCertificates();
        var receivedClientCertificate = false;
        var server = new FakeZooKeeperServer(
            async (stream, token) =>
            {
                using var tls = new SslStream(stream, leaveInnerStreamOpen: true);
                await tls.AuthenticateAsServerAsync(certificates.ServerOptions(mutual), token)
                    .ConfigureAwait(false);
                receivedClientCertificate = tls.RemoteCertificate is not null;
                await ServeSessionAsync(tls, token).ConfigureAwait(false);
            },
            TestContext.Current.CancellationToken
        );
        await using var serverLifetime = server.ConfigureAwait(true);
        using var options = ClientOptionsSnapshot.Capture(
            new ZooKeeperClientOptions
            {
                ConnectionString = $"zk.test:{server.Port}",
                Tls = certificates.ClientOptions(mutual),
            }
        );
        var resolver = Resolver();
        var random = new SessionRandom(new Random(1));
        var client = await ZooKeeperClient
            .ConnectCoreAsync(
                options,
                TimeProvider.System,
                new SocketTransportConnector(resolver, random),
                random,
                TestContext.Current.CancellationToken
            )
            .ConfigureAwait(true);
        await using var clientLifetime = client.ConfigureAwait(true);
        await client.SyncAsync("/tls", TestContext.Current.CancellationToken).ConfigureAwait(true);
        await client.CloseAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
        await server.Completion.ConfigureAwait(true);
        Assert.Equal(mutual, receivedClientCertificate);
        Assert.Equal(ZooKeeperCloseStatus.Acknowledged, client.Session.CloseStatus);
        // Driver-owned copies can be disposed without invalidating application handles.
        Assert.True(certificates.Client.HasPrivateKey);
        Assert.NotEmpty(certificates.Root.RawData);
        await resolver
            .Received(1)
            .ResolveAsync("zk.test", Arg.Any<CancellationToken>())
            .ConfigureAwait(true);
    }

    [Fact(Timeout = 15000)]
    public async Task ReconnectRotatesBothCertificateAndRootWhileResumingTheSession()
    {
        var token = TestContext.Current.CancellationToken;
        using var first = new EphemeralCertificates();
        using var second = new EphemeralCertificates();
        using var socket = new TcpListener(IPAddress.Loopback, 0);
        socket.Start();
        int calls = 0;
        var rotated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var options = new ZooKeeperClientOptions
        {
            ConnectionString = $"zk.test:{((IPEndPoint)socket.LocalEndpoint).Port}",
            Tls = ZooKeeperTlsOptions.RequiredWithProvider(
                _ =>
                {
                    var certs = Interlocked.Increment(ref calls) == 1 ? first : second;
                    return ValueTask.FromResult(
                        new ZooKeeperTlsCredentials(certs.Client, [certs.Root])
                    );
                },
                X509RevocationMode.NoCheck
            ),
        };
        var peer = ServeBothAsync();
        var random = new SessionRandom(new Random(1));
        var client = await ZooKeeperClient.ConnectCoreAsync(
            ClientOptionsSnapshot.Capture(options),
            TimeProvider.System,
            new SocketTransportConnector(Resolver(), random),
            random,
            token
        );
        await using var owner = client.ConfigureAwait(true);
        await client.SyncAsync("/first", token);
        await rotated.Task.WaitAsync(token);
        await client.WaitForConnectionAsync(token);
        await client.SyncAsync("/second", token);
        Assert.Equal(123, client.Session.SessionId);
        Assert.Equal(2, calls);
        await client.CloseAsync(token);
        await peer.ConfigureAwait(true);

        async Task ServeBothAsync()
        {
            foreach (var certs in new[] { first, second })
            {
                using var tcp = await socket.AcceptTcpClientAsync(token).ConfigureAwait(false);
                using var tls = new SslStream(tcp.GetStream());
                await tls.AuthenticateAsServerAsync(certs.ServerOptions(true), token)
                    .ConfigureAwait(false);
                Assert.Equal(
                    certs.Client.GetCertHashString(),
                    tls.RemoteCertificate!.GetCertHashString()
                );
                await ServeSessionAsync(
                        tls,
                        token,
                        certs == first,
                        certs == second ? rotated : null,
                        certs == first ? 0 : 123
                    )
                    .ConfigureAwait(false);
            }
        }
    }

    [Theory(Timeout = 10000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProviderFailureOrDeadlinePreservesSafeTlsReason(bool timeout)
    {
        var token = TestContext.Current.CancellationToken;
        var began = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new TaskCompletionSource<ZooKeeperTlsCredentials>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var time = new ManualTimeProvider();
        var duration = TimeSpan.FromSeconds(1);
        var random = new SessionRandom(new Random(1));
        var options = ClientOptionsSnapshot.Capture(
            new ZooKeeperClientOptions
            {
                ConnectionString = "zk.test",
                InitialConnectTimeout = duration,
                ConnectAttemptTimeout = duration,
                Tls = ZooKeeperTlsOptions.RequiredWithProvider(_ =>
                {
                    began.TrySetResult();
                    return new ValueTask<ZooKeeperTlsCredentials>(provider.Task);
                }),
            }
        );
        var connection = ZooKeeperClient.ConnectCoreAsync(
            options,
            time,
            new SocketTransportConnector(Resolver(), random),
            random,
            token
        );
        await began.Task.WaitAsync(token).ConfigureAwait(true);
        if (!timeout)
        {
            provider.SetException(new InvalidOperationException("private provider diagnostics"));
        }
        // Advance in bounded increments so both the attempt and any retry backoff expire.
        while (!connection.IsCompleted)
        {
            time.Advance(duration);
            await Task.Delay(1, token).ConfigureAwait(true);
        }
        var error = await Assert
            .ThrowsAsync<ZooKeeperConnectionException>(() => connection)
            .ConfigureAwait(true);
        Assert.Equal(ZooKeeperTlsFailureReason.ProviderFailed, error.TlsFailureReason);
        Assert.DoesNotContain(
            "private provider diagnostics",
            error.ToString(),
            StringComparison.Ordinal
        );
        provider.TrySetCanceled(token);
    }

    [Theory(Timeout = 10000)]
    [InlineData("name")]
    [InlineData("trust")]
    [InlineData("expired")]
    [Trait("Acceptance", "T20")]
    public async Task InvalidCertificateFailsWithoutSendingZooKeeperBytes(string failure)
    {
        using var certificates = new EphemeralCertificates(expired: failure == "expired");
        var applicationBytes = 0;
        var server = new FakeZooKeeperServer(
            async (stream, token) =>
            {
                using var tls = new SslStream(stream, leaveInnerStreamOpen: true);
                try
                {
                    await tls.AuthenticateAsServerAsync(certificates.ServerOptions(false), token)
                        .ConfigureAwait(false);
                    var buffer = new byte[64];
                    applicationBytes = await tls.ReadAsync(buffer, token).ConfigureAwait(false);
                }
                catch (AuthenticationException) { }
                catch (IOException) { }
            },
            TestContext.Current.CancellationToken
        );
        await using var serverLifetime = server.ConfigureAwait(true);
        using var options = ClientOptionsSnapshot.Capture(
            new ZooKeeperClientOptions
            {
                ConnectionString = $"zk.test:{server.Port}",
                Tls = ZooKeeperTlsOptions.Required(
                    X509RevocationMode.NoCheck,
                    trustRoots: failure == "trust" ? [] : [certificates.Root],
                    serverName: failure == "name" ? "wrong.test" : null
                ),
            }
        );
        var connector = new SocketTransportConnector(Resolver(), new SessionRandom(new Random(1)));
        var error = await Assert
            .ThrowsAsync<ZooKeeperConnectionException>(() =>
                connector.ConnectAsync(
                    new ZooKeeperEndpoint("zk.test", server.Port),
                    options,
                    new TransportDeadline(
                        TimeProvider.System,
                        options.Settings.ConnectAttemptTimeout
                    ),
                    TestContext.Current.CancellationToken
                )
            )
            .ConfigureAwait(true);
        Assert.Equal(ZooKeeperConnectionFailure.Tls, error.Failure);
        await server.Completion.ConfigureAwait(true);
        Assert.Equal(
            failure switch
            {
                "name" => ZooKeeperTlsFailureReason.NameMismatch,
                "expired" => ZooKeeperTlsFailureReason.InvalidValidityPeriod,
                _ => ZooKeeperTlsFailureReason.UntrustedChain,
            },
            error.TlsFailureReason
        );
        Assert.Equal(0, applicationBytes);
    }

    [Theory(Timeout = 10000)]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Acceptance", "T20")]
    public async Task CertificateFailureSurvivesALaterAddressDeadlineButHonorsCallerCancellation(
        bool cancelCaller
    )
    {
        var token = TestContext.Current.CancellationToken;
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        using var certificates = new EphemeralCertificates();
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var time = new ManualTimeProvider();
        var random = new SessionRandom(new Random(1));
        var resolver = Substitute.For<IHostResolver>();
        resolver
            .ResolveAsync("zk.test", Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(new[] { IPAddress.Loopback, IPAddress.Loopback }));
        var deadline = TimeSpan.FromSeconds(5);
        var options = new ZooKeeperClientOptions
        {
            ConnectionString = $"zk.test:{port}",
            Tls = ZooKeeperTlsOptions.Required(
                X509RevocationMode.NoCheck,
                trustRoots: [certificates.Root],
                serverName: "wrong.test"
            ),
            InitialConnectTimeout = deadline,
            ConnectAttemptTimeout = deadline,
        };
        var connecting = ZooKeeperClient.ConnectCoreAsync(
            ClientOptionsSnapshot.Capture(options),
            time,
            new SocketTransportConnector(resolver, random),
            random,
            stop.Token
        );
        try
        {
            using (var first = await listener.AcceptTcpClientAsync(token))
            using (var tls = new SslStream(first.GetStream()))
            {
                try
                {
                    await tls.AuthenticateAsServerAsync(certificates.ServerOptions(false), token);
                    await tls.ReadExactlyAsync(new byte[1], token);
                }
                catch (AuthenticationException) { }
                catch (IOException) { }
            }
            using var second = await listener.AcceptTcpClientAsync(token);
            await second.GetStream().ReadExactlyAsync(new byte[1], token);
            if (cancelCaller)
            {
                await stop.CancelAsync();
                var canceled = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                    connecting
                );
                Assert.Equal(stop.Token, canceled.CancellationToken);
            }
            else
            {
                time.Advance(deadline);
                var failure = await Assert.ThrowsAsync<ZooKeeperConnectionException>(() =>
                    connecting
                );
                Assert.Equal(ZooKeeperConnectionFailure.Tls, failure.Failure);
            }
        }
        finally
        {
            await stop.CancelAsync();
            try
            {
                var unexpected = await connecting.ConfigureAwait(true);
                await unexpected.DisposeAsync().ConfigureAwait(true);
            }
            catch (OperationCanceledException) { }
            catch (ZooKeeperConnectionException) { }
        }
    }

    [Fact(Timeout = 10000)]
    [Trait("Acceptance", "T20")]
    public async Task PlaintextEndpointDoesNotTriggerATlsDowngrade()
    {
        var firstByte = 0;
        var server = new FakeZooKeeperServer(
            async (stream, token) =>
            {
                var buffer = new byte[1];
                await stream.ReadExactlyAsync(buffer, token).ConfigureAwait(false);
                firstByte = buffer[0];
                await stream
                    .WriteAsync("plaintext endpoint"u8.ToArray(), token)
                    .ConfigureAwait(false);
            },
            TestContext.Current.CancellationToken
        );
        await using var serverLifetime = server.ConfigureAwait(true);
        using var options = ClientOptionsSnapshot.Capture(
            new ZooKeeperClientOptions { ConnectionString = $"zk.test:{server.Port}" }
        );
        var connector = new SocketTransportConnector(Resolver(), new SessionRandom(new Random(1)));
        var error = await Assert
            .ThrowsAsync<ZooKeeperConnectionException>(() =>
                connector.ConnectAsync(
                    new ZooKeeperEndpoint("zk.test", server.Port),
                    options,
                    new TransportDeadline(
                        TimeProvider.System,
                        options.Settings.ConnectAttemptTimeout
                    ),
                    TestContext.Current.CancellationToken
                )
            )
            .ConfigureAwait(true);
        Assert.Equal(ZooKeeperConnectionFailure.Tls, error.Failure);
        Assert.Equal(0x16, firstByte); // TLS handshake record, never a ZooKeeper frame.
    }

    [Fact(Timeout = 10000)]
    [Trait("Acceptance", "T20")]
    public async Task MissingRequiredClientCertificateCannotEstablishAZooKeeperSession()
    {
        using var certificates = new EphemeralCertificates();
        var sessionHandshake = false;
        var server = new FakeZooKeeperServer(
            async (stream, token) =>
            {
                using var tls = new SslStream(stream, leaveInnerStreamOpen: true);
                try
                {
                    await tls.AuthenticateAsServerAsync(certificates.ServerOptions(true), token)
                        .ConfigureAwait(false);
                    var packet = await new FrameReader(tls, new ProtocolLimits())
                        .ReadAsync(token)
                        .ConfigureAwait(false);
                    sessionHandshake = packet is not null;
                }
                catch (AuthenticationException) { }
                catch (IOException) { }
            },
            TestContext.Current.CancellationToken
        );
        await using var serverLifetime = server.ConfigureAwait(true);
        var settings = new ZooKeeperClientOptions
        {
            ConnectionString = $"zk.test:{server.Port}",
            Tls = certificates.ClientOptions(false),
            InitialConnectTimeout = TimeSpan.FromMilliseconds(500),
            ConnectAttemptTimeout = TimeSpan.FromMilliseconds(500),
        };
        using var options = ClientOptionsSnapshot.Capture(settings);
        var random = new SessionRandom(new Random(1));
        await Assert
            .ThrowsAsync<ZooKeeperConnectionException>(async () =>
            {
                var unexpected = await ZooKeeperClient
                    .ConnectCoreAsync(
                        options,
                        TimeProvider.System,
                        new SocketTransportConnector(Resolver(), random),
                        random,
                        TestContext.Current.CancellationToken
                    )
                    .ConfigureAwait(true);
                await using var unexpectedLifetime = unexpected.ConfigureAwait(true);
            })
            .ConfigureAwait(true);
        await server.Completion.ConfigureAwait(true);
        Assert.False(sessionHandshake);
    }

    [Fact(Timeout = 10000)]
    [Trait("Acceptance", "T08")]
    public async Task StalledAddressLeavesTimeForAuthenticatedFallbackAfterDns()
    {
        var token = TestContext.Current.CancellationToken;
        using var certificates = new EphemeralCertificates();
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var time = new ManualTimeProvider();
        var resolver = Substitute.For<IHostResolver>();
        resolver
            .ResolveAsync("zk.test", Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                time.Advance(TimeSpan.FromSeconds(2));
                return Task.FromResult(new[] { IPAddress.Loopback, IPAddress.Loopback });
            });
        var random = new SessionRandom(new Random(1));
        using var options = ClientOptionsSnapshot.Capture(
            new ZooKeeperClientOptions
            {
                ConnectionString = $"zk.test:{((IPEndPoint)listener.LocalEndpoint).Port}",
                Tls = certificates.ClientOptions(false),
                InitialConnectTimeout = TimeSpan.FromSeconds(6),
                ConnectAttemptTimeout = TimeSpan.FromSeconds(20),
            }
        );
        async Task ConnectAndCloseAsync()
        {
            var client = await ZooKeeperClient
                .ConnectCoreAsync(
                    options,
                    time,
                    new SocketTransportConnector(resolver, random),
                    random,
                    token
                )
                .ConfigureAwait(true);
            await using var lifetime = client.ConfigureAwait(true);
            await client.CloseAsync(token).ConfigureAwait(true);
        }

        async Task ServeSecondAddressAsync()
        {
            using var first = await listener.AcceptTcpClientAsync(token).ConfigureAwait(true);
            await first.GetStream().ReadExactlyAsync(new byte[1], token).ConfigureAwait(true);
            // DNS consumed two seconds. The two addresses share the remaining four.
            time.Advance(TimeSpan.FromSeconds(2));
            using var second = await listener.AcceptTcpClientAsync(token).ConfigureAwait(true);
            using var tls = new SslStream(second.GetStream());
            await tls.AuthenticateAsServerAsync(certificates.ServerOptions(false), token)
                .ConfigureAwait(true);
            await ServeSessionAsync(tls, token).ConfigureAwait(true);
        }

        await Task.WhenAll(ConnectAndCloseAsync(), ServeSecondAddressAsync()).ConfigureAwait(true);
        await resolver.Received(1).ResolveAsync("zk.test", Arg.Any<CancellationToken>());
    }

    [Fact(Timeout = 10000)]
    [Trait("Acceptance", "T08")]
    public async Task DnsIsRefreshedAndEveryAddressIsTriedWithoutLeakingFailedSockets()
    {
        var server = new FakeZooKeeperServer(
            async (stream, token) =>
            {
                var buffer = new byte[1];
                await stream.ReadExactlyAsync(buffer, token).ConfigureAwait(false);
            },
            TestContext.Current.CancellationToken
        );
        await using var serverLifetime = server.ConfigureAwait(true);
        var resolver = Substitute.For<IHostResolver>();
        resolver
            .ResolveAsync("zk.test", Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(new[] { IPAddress.IPv6Loopback, IPAddress.Loopback }));
        var connector = new SocketTransportConnector(resolver, new SessionRandom(new Random(0)));
        using var options = ClientOptionsSnapshot.Capture(SessionFixture.Options());
        var endpoint = new ZooKeeperEndpoint("zk.test", server.Port);
        var connection = await connector
            .ConnectAsync(
                endpoint,
                options,
                new TransportDeadline(TimeProvider.System, options.Settings.ConnectAttemptTimeout),
                TestContext.Current.CancellationToken
            )
            .ConfigureAwait(true);
        await using var connectionLifetime = connection.ConfigureAwait(true);
        await connection
            .Stream.WriteAsync(new byte[] { 1 }, TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        await connection.DisposeAsync().ConfigureAwait(true);
        await server.Completion.ConfigureAwait(true);
        await Assert
            .ThrowsAsync<ZooKeeperConnectionException>(() =>
                connector.ConnectAsync(
                    endpoint,
                    options,
                    new TransportDeadline(
                        TimeProvider.System,
                        options.Settings.ConnectAttemptTimeout
                    ),
                    TestContext.Current.CancellationToken
                )
            )
            .ConfigureAwait(true);
        await resolver
            .Received(2)
            .ResolveAsync("zk.test", Arg.Any<CancellationToken>())
            .ConfigureAwait(true);
    }

    [Fact]
    public void CertificateSnapshotsOwnIndependentHandles()
    {
        using var certificates = new EphemeralCertificates();
        using var options = ClientOptionsSnapshot.Capture(
            new ZooKeeperClientOptions
            {
                ConnectionString = "zk.test",
                Tls = certificates.ClientOptions(true),
            }
        );
        certificates.Root.Dispose();
        certificates.Client.Dispose();
        Assert.True(options.Settings.Tls.ClientCertificate!.HasPrivateKey);
        Assert.NotEmpty(Assert.Single(options.Settings.Tls.TrustRoots).RawData);
    }

    private static IHostResolver Resolver()
    {
        var resolver = Substitute.For<IHostResolver>();
        resolver
            .ResolveAsync("zk.test", Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(new[] { IPAddress.Loopback }));
        return resolver;
    }

    internal static async Task ServeSessionAsync(
        Stream stream,
        CancellationToken token,
        bool dropAfterSync = false,
        TaskCompletionSource? resumed = null,
        long? expectedSession = null
    )
    {
        var limits = new ProtocolLimits();
        var reader = new FrameReader(stream, limits);
        var writer = new FrameWriter(stream, limits);
        var connect = await reader.ReadAsync(token).ConfigureAwait(false);
        Assert.NotNull(connect);
        if (expectedSession.HasValue)
        {
            var input = new JuteReader(connect, limits);
            _ = input.ReadInt32();
            _ = input.ReadInt64();
            _ = input.ReadInt32();
            Assert.Equal(expectedSession.Value, input.ReadInt64());
        }
        var bytes = new byte[64];
        var handshake = new JuteWriter(bytes, limits);
        handshake.WriteInt32(0);
        handshake.WriteInt32(30000);
        handshake.WriteInt64(123);
        handshake.WriteBuffer(new byte[16]);
        handshake.WriteBoolean(false);
        await writer
            .WriteFrameAsync(PeerTests.Frame(bytes[..handshake.WrittenCount]), token)
            .ConfigureAwait(false);
        resumed?.TrySetResult();
        while (true)
        {
            var packet =
                await reader.ReadAsync(token).ConfigureAwait(false)
                ?? throw new EndOfStreamException();
            var input = new JuteReader(packet, limits);
            var header = input.ReadRequestHeader();
            var path = header.Opcode == 9 ? input.ReadString() : null;
            await writer
                .WriteFrameAsync(PeerTests.Frame(SessionPeer.Packet(header.Xid, path: path)), token)
                .ConfigureAwait(false);
            if (header.Opcode == -11 || (dropAfterSync && path is not null))
            {
                return;
            }
        }
    }
}

internal sealed class EphemeralCertificates : IDisposable
{
    internal X509Certificate2 Root { get; }
    internal X509Certificate2 Server { get; }
    internal X509Certificate2 Client { get; }

    internal EphemeralCertificates(bool expired = false)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=ZkLink ephemeral test root",
            key,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1
        );
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        request.CertificateExtensions.Add(
            new X509KeyUsageExtension(
                X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign,
                true
            )
        );
        Root = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-2),
            DateTimeOffset.UtcNow.AddDays(2)
        );
        Server = Leaf(server: true, expired);
        Client = Leaf(server: false, expired: false);
    }

    private X509Certificate2 Leaf(bool server, bool expired)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest(
            server ? "CN=zk.test" : "CN=zk-client",
            key,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1
        );
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(
            new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true)
        );
        request.CertificateExtensions.Add(
            new X509EnhancedKeyUsageExtension(
                new OidCollection { new Oid(server ? "1.3.6.1.5.5.7.3.1" : "1.3.6.1.5.5.7.3.2") },
                true
            )
        );
        if (server)
        {
            var names = new SubjectAlternativeNameBuilder();
            names.AddDnsName("zk.test");
            request.CertificateExtensions.Add(names.Build());
        }
        using var publicCertificate = request.Create(
            Root,
            DateTimeOffset.UtcNow.AddDays(-1),
            expired ? DateTimeOffset.UtcNow.AddHours(-1) : DateTimeOffset.UtcNow.AddDays(1),
            RandomNumberGenerator.GetBytes(16)
        );
        var certificate = publicCertificate.CopyWithPrivateKey(key);
        if (!OperatingSystem.IsWindows())
        {
            return certificate;
        }
        // Schannel requires a named key container. DefaultKeySet removes the
        // temporary imported key when the certificate is disposed; do not persist it.
        using (certificate)
        {
            byte[] pkcs12 = certificate.Export(X509ContentType.Pkcs12);
            try
            {
                return X509CertificateLoader.LoadPkcs12(
                    pkcs12,
                    null,
                    X509KeyStorageFlags.DefaultKeySet
                );
            }
            finally
            {
                CryptographicOperations.ZeroMemory(pkcs12);
            }
        }
    }

    // Ephemeral roots have no revocation service. Production defaults remain Online.
    internal ZooKeeperTlsOptions ClientOptions(bool mutual) =>
        ZooKeeperTlsOptions.Required(X509RevocationMode.NoCheck, mutual ? Client : null, [Root]);

    internal SslServerAuthenticationOptions ServerOptions(bool mutual) =>
        new()
        {
            ServerCertificate = Server,
            ClientCertificateRequired = mutual,
            EnabledSslProtocols = SslProtocols.None,
            CertificateChainPolicy = new X509ChainPolicy
            {
                TrustMode = X509ChainTrustMode.CustomRootTrust,
                CustomTrustStore = { Root },
                RevocationMode = X509RevocationMode.NoCheck,
            },
        };

    public void Dispose()
    {
        Client.Dispose();
        Server.Dispose();
        Root.Dispose();
    }
}
