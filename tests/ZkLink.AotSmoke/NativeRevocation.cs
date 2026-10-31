using System.Buffers.Binary;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using ZkLink.Protocol.Tests.Sessions;

namespace ZkLink.AotSmoke;

// A loopback peer isolates native chain evaluation from JVM certificate policy.
// It implements only connect and acknowledged close; rejected TLS admits no bytes.
internal static class NativeRevocation
{
    private const int PrefixBytes = sizeof(int);
    private const int ReplyHeaderBytes = sizeof(int) + sizeof(long) + sizeof(int);
    private const int PasswordBytes = 16;
    private const int MaximumFrameBytes = 1_048_575;
    private const int NegotiatedMilliseconds = 30_000;
    private const long SessionId = 1;
    private const int MaximumPeerConnections = 32;
    private const int RevocationEndpointsPerCertificate = 2;
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan AttemptGuard = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan PeerCleanupGuard = TimeSpan.FromSeconds(2);

    private enum StatusCase
    {
        Valid,
        Revoked,
        Unavailable,
        InvalidSignature,
    }

    internal static async Task VerifyAsync(CancellationToken token)
    {
        foreach (
            var scenario in new[]
            {
                StatusCase.Valid,
                StatusCase.Revoked,
                StatusCase.Unavailable,
                StatusCase.InvalidSignature,
            }
        )
        {
            await VerifyCaseAsync(scenario, token).ConfigureAwait(false);
        }
        await VerifyCancellationAsync(token).ConfigureAwait(false);
    }

    private static async Task VerifyCancellationAsync(CancellationToken token)
    {
        var authority = new RevocationAuthority(holdResponse: true, cancellationToken: token);
        await using var lifetime = authority.ConfigureAwait(false);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        var connection = ConnectAsync(
            authority,
            X509RevocationMode.Online,
            accepted: false,
            cancellation.Token
        );
        try
        {
            await authority.Requested.WaitAsync(Guard, token).ConfigureAwait(false);
            await cancellation.CancelAsync().ConfigureAwait(false);
            if (OperatingSystem.IsMacOS())
            {
                // macOS does not honor per-URL retrieval timeouts. Release owned
                // native work, then prove that cancellation prevents admission.
                authority.Release();
            }
            try
            {
                await connection.WaitAsync(Guard, token).ConfigureAwait(false);
                throw new InvalidOperationException("Native TLS cancellation was ignored.");
            }
            catch (OperationCanceledException failure)
            {
                NativeSettings.Require(failure.CancellationToken == cancellation.Token);
            }
            await Console
                .Out.WriteLineAsync("Native revocation case HeldCancellation: PASS")
                .ConfigureAwait(false);
        }
        finally
        {
            authority.Release();
            await cancellation.CancelAsync().ConfigureAwait(false);
            try
            {
                await connection.ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
            catch (ZooKeeperConnectionException) { }
        }
    }

    private static async Task VerifyCaseAsync(StatusCase scenario, CancellationToken token)
    {
        var authority = new RevocationAuthority(
            revoked: scenario == StatusCase.Revoked,
            unavailable: scenario == StatusCase.Unavailable,
            invalidSignature: scenario == StatusCase.InvalidSignature,
            maximumRequests: MaximumPeerConnections * RevocationEndpointsPerCertificate,
            cancellationToken: token
        );
        await using var lifetime = authority.ConfigureAwait(false);
        await ConnectAsync(authority, X509RevocationMode.NoCheck, accepted: true, token)
            .ConfigureAwait(false);
        NativeSettings.Require(authority.Requests == 0);
        await Console
            .Out.WriteLineAsync("Native NoCheck control " + scenario + ": PASS")
            .ConfigureAwait(false);
        await ConnectAsync(
                authority,
                X509RevocationMode.Online,
                accepted: scenario == StatusCase.Valid,
                token
            )
            .ConfigureAwait(false);
        NativeSettings.Require(authority.Requests > 0);
        await Console
            .Out.WriteLineAsync("Native revocation case " + scenario + ": PASS")
            .ConfigureAwait(false);
    }

    private static async Task ConnectAsync(
        RevocationAuthority authority,
        X509RevocationMode mode,
        bool accepted,
        CancellationToken token
    )
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(Guard + PeerCleanupGuard);
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int applicationFrames = 0;
        var peer = ServeAsync();
        try
        {
            var settings = new ZooKeeperClientOptions
            {
                ConnectionString = $"127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}",
                Tls = ZooKeeperTlsOptions.Required(
                    mode,
                    trustRoots: [authority.Root],
                    serverName: authority.ServerName
                ),
                InitialConnectTimeout = Guard,
                ConnectAttemptTimeout = AttemptGuard,
                CloseTimeout = Guard,
            };
            if (accepted)
            {
                var client = await ZooKeeperClient
                    .ConnectAsync(settings, token)
                    .ConfigureAwait(false);
                await using var clientLifetime = client.ConfigureAwait(false);
                await NativeSettings.CloseAsync(client, deadline.Token).ConfigureAwait(false);
            }
            else
            {
                try
                {
                    var unexpected = await ZooKeeperClient
                        .ConnectAsync(settings, token)
                        .ConfigureAwait(false);
                    await unexpected.DisposeAsync().ConfigureAwait(false);
                    throw new InvalidOperationException(
                        "Invalid native revocation status was accepted."
                    );
                }
                catch (ZooKeeperConnectionException failure)
                {
                    NativeSettings.Require(failure.Failure == ZooKeeperConnectionFailure.Tls);
                }
            }
            if (accepted)
            {
                await peer.ConfigureAwait(false);
            }
        }
        finally
        {
            await deadline.CancelAsync().ConfigureAwait(false);
            listener.Stop();
            try
            {
                await peer.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested) { }
            catch (SocketException) when (deadline.IsCancellationRequested) { }
        }

        NativeSettings.Require(applicationFrames == (accepted ? 2 : 0));

        async Task ServeAsync()
        {
            for (int attempt = 0; attempt < MaximumPeerConnections; attempt++)
            {
                using var connection = await listener
                    .AcceptTcpClientAsync(deadline.Token)
                    .ConfigureAwait(false);
                using var tls = new SslStream(connection.GetStream(), leaveInnerStreamOpen: true);
                try
                {
                    await tls.AuthenticateAsServerAsync(
                            new SslServerAuthenticationOptions
                            {
                                ServerCertificate = authority.Server,
                            },
                            deadline.Token
                        )
                        .ConfigureAwait(false);
                    var prefix = new byte[PrefixBytes];
                    int count = await tls.ReadAsync(prefix.AsMemory(0, 1), deadline.Token)
                        .ConfigureAwait(false);
                    if (count == 0)
                    {
                        continue;
                    }
                    await tls.ReadExactlyAsync(prefix.AsMemory(1), deadline.Token)
                        .ConfigureAwait(false);
                    byte[] connect = await ReadBodyAsync(prefix).ConfigureAwait(false);
                    CryptographicOperations.ZeroMemory(connect);
                    Interlocked.Increment(ref applicationFrames);
                    NativeSettings.Require(accepted);
                    byte[] handshake = new byte[
                        sizeof(int)
                            + sizeof(int)
                            + sizeof(long)
                            + sizeof(int)
                            + PasswordBytes
                            + sizeof(byte)
                    ];
                    BinaryPrimitives.WriteInt32BigEndian(
                        handshake.AsSpan(sizeof(int)),
                        NegotiatedMilliseconds
                    );
                    BinaryPrimitives.WriteInt64BigEndian(
                        handshake.AsSpan(2 * sizeof(int)),
                        SessionId
                    );
                    BinaryPrimitives.WriteInt32BigEndian(
                        handshake.AsSpan(2 * sizeof(int) + sizeof(long)),
                        PasswordBytes
                    );
                    await WriteFrameAsync(handshake).ConfigureAwait(false);
                    await tls.ReadExactlyAsync(prefix, deadline.Token).ConfigureAwait(false);
                    byte[] close = await ReadBodyAsync(prefix).ConfigureAwait(false);
                    Interlocked.Increment(ref applicationFrames);
                    NativeSettings.Require(
                        close.Length >= 2 * sizeof(int)
                            && BinaryPrimitives.ReadInt32BigEndian(close.AsSpan(sizeof(int)))
                                == (int)ZooKeeperOperation.CloseSession
                    );
                    var reply = new byte[ReplyHeaderBytes];
                    close.AsSpan(0, sizeof(int)).CopyTo(reply);
                    CryptographicOperations.ZeroMemory(close);
                    await WriteFrameAsync(reply).ConfigureAwait(false);
                    return;

                    async Task<byte[]> ReadBodyAsync(byte[] header)
                    {
                        int length = BinaryPrimitives.ReadInt32BigEndian(header);
                        NativeSettings.Require(length > 0 && length <= MaximumFrameBytes);
                        var body = new byte[length];
                        await tls.ReadExactlyAsync(body, deadline.Token).ConfigureAwait(false);
                        return body;
                    }

                    async Task WriteFrameAsync(byte[] body)
                    {
                        BinaryPrimitives.WriteInt32BigEndian(prefix, body.Length);
                        try
                        {
                            await tls.WriteAsync(prefix, deadline.Token).ConfigureAwait(false);
                            await tls.WriteAsync(body, deadline.Token).ConfigureAwait(false);
                        }
                        finally
                        {
                            CryptographicOperations.ZeroMemory(body);
                        }
                    }
                }
                catch (AuthenticationException) when (!accepted) { }
                catch (IOException) when (!accepted) { }
            }
            throw new InvalidOperationException("Native TLS peer connection bound exceeded.");
        }
    }
}
