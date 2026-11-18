using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using ZkLink.Configuration;
using ZkLink.Sessions;

namespace ZkLink.Transport;

internal interface ITransportConnector
{
    Task<TransportConnection> ConnectAsync(
        ZooKeeperEndpoint endpoint,
        ClientOptionsSnapshot options,
        TransportDeadline deadline,
        CancellationToken cancellationToken
    );
}

internal readonly struct TransportDeadline(TimeProvider clock, TimeSpan duration)
{
    private readonly long _started = clock.GetTimestamp();

    internal CancellationTokenSource? StartAddressTimeout(int remainingAddresses)
    {
        // The session already owns the deadline for the last (or only) address.
        if (remainingAddresses == 1)
        {
            return null;
        }
        var remaining = duration - clock.GetElapsedTime(_started);
        var slice = TimeSpan.FromTicks(Math.Max(0, remaining.Ticks) / remainingAddresses);
        return new CancellationTokenSource(slice, clock);
    }
}

internal interface IHostResolver
{
    Task<IPAddress[]> ResolveAsync(string host, CancellationToken cancellationToken);
}

internal sealed class HostResolver : IHostResolver
{
    public Task<IPAddress[]> ResolveAsync(string host, CancellationToken cancellationToken) =>
        IPAddress.TryParse(host, out var address)
            ? Task.FromResult(new[] { address })
            : Dns.GetHostAddressesAsync(host, cancellationToken);
}

internal sealed class SocketTransportConnector(IHostResolver resolver, SessionRandom random)
    : ITransportConnector
{
    private int _providerBusy;
    private static readonly TimeSpan MinimumRetrievalTimeout = TimeSpan.FromMilliseconds(1);

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Reliability",
        "CA2000",
        Justification = "The socket transfers to NetworkStream, then SslStream, then TransportConnection; every untransferred local is disposed in finally."
    )]
    public async Task<TransportConnection> ConnectAsync(
        ZooKeeperEndpoint endpoint,
        ClientOptionsSnapshot options,
        TransportDeadline deadline,
        CancellationToken cancellationToken
    )
    {
        var provider = options.Settings.Tls.CredentialsProvider;
        using var credentials = provider is null
            ? null
            : await LoadCredentialsAsync(provider, endpoint, cancellationToken)
                .ConfigureAwait(false);
        var addresses = await resolver
            .ResolveAsync(endpoint.Host, cancellationToken)
            .ConfigureAwait(false);
        random.Shuffle(addresses);
        ZooKeeperTlsFailureReason? tlsFailure = null;
        SocketError? socketError = null;
        var addressTimedOut = false;
        var remainingAddresses = addresses.Count(IsUsable);
        foreach (var address in addresses)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            if (!IsUsable(address))
            {
                continue;
            }
            using var timeout = deadline.StartAddressTimeout(remainingAddresses--);
            using var linked = timeout is null
                ? null
                : CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, cancellationToken);
            var addressToken = linked?.Token ?? cancellationToken;
            Socket? socket = new(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp)
            {
                NoDelay = true,
            };
            NetworkStream? network = null;
            SslStream? ssl = null;
            ZooKeeperTlsFailureReason? certificateFailure = null;
            try
            {
                await socket
                    .ConnectAsync(new IPEndPoint(address, endpoint.Port), addressToken)
                    .ConfigureAwait(false);
                network = new NetworkStream(socket, ownsSocket: true);
                socket = null;
                if (options.Settings.Tls.Enabled)
                {
                    ssl = new SslStream(
                        network,
                        leaveInnerStreamOpen: false,
                        (_, _, chain, errors) =>
                        {
                            if (errors == SslPolicyErrors.None)
                            {
                                return true;
                            }
                            certificateFailure = ClassifyCertificate(errors, chain);
                            return false;
                        }
                    );
                    network = null;
                    var tls = options.Settings.Tls;
                    var authentication = new SslClientAuthenticationOptions
                    {
                        AllowTlsResume = provider is null,
                        TargetHost = tls.ServerName ?? endpoint.Host,
                        EnabledSslProtocols = SslProtocols.None,
                        CertificateRevocationCheckMode = tls.RevocationMode,
                    };
                    // Native chain evaluation can outlive token cancellation. Bound each
                    // download where the platform supports it; macOS ignores this value.
                    var policy = new X509ChainPolicy
                    {
                        RevocationMode = tls.RevocationMode,
                        UrlRetrievalTimeout =
                            options.Settings.ConnectAttemptTimeout < MinimumRetrievalTimeout
                                ? MinimumRetrievalTimeout
                                : options.Settings.ConnectAttemptTimeout,
                    };
                    authentication.CertificateChainPolicy = policy;
                    var certificate = credentials?.ClientCertificate ?? tls.ClientCertificate;
                    var roots = credentials?.TrustRoots ?? tls.TrustRoots;
                    if (certificate is not null)
                    {
                        authentication.ClientCertificates = new X509CertificateCollection
                        {
                            certificate,
                        };
                    }
                    if (roots.Count != 0)
                    {
                        policy.TrustMode = X509ChainTrustMode.CustomRootTrust;
                        foreach (var root in roots)
                        {
                            policy.CustomTrustStore.Add(root);
                        }
                    }
                    await ssl.AuthenticateAsClientAsync(authentication, addressToken)
                        .ConfigureAwait(false);
                }
                addressToken.ThrowIfCancellationRequested();
                var result = new TransportConnection(
                    (Stream?)ssl ?? network!,
                    endpoint,
                    options.Limits
                );
                ssl = null;
                network = null;
                return result;
            }
            // Native TLS validation can finish with an authentication or I/O error
            // after the deadline. Only earlier failures count as TLS evidence.
            catch (Exception error)
                when (addressToken.IsCancellationRequested
                    && error
                        is OperationCanceledException
                            or AuthenticationException
                            or IOException
                            or SocketException
                )
            {
                addressTimedOut = true;
            }
            catch (AuthenticationException)
            {
                tlsFailure =
                    certificateFailure ?? tlsFailure ?? ZooKeeperTlsFailureReason.HandshakeFailed;
            }
            catch (SocketException error)
            {
                socketError = error.SocketErrorCode;
            }
            catch (IOException)
            {
                if (options.Settings.Tls.Enabled)
                {
                    tlsFailure =
                        certificateFailure
                        ?? tlsFailure
                        ?? ZooKeeperTlsFailureReason.HandshakeFailed;
                }
            }
            finally
            {
                socket?.Dispose();
                if (ssl is not null)
                {
                    await ssl.DisposeAsync().ConfigureAwait(false);
                }
                if (network is not null)
                {
                    await network.DisposeAsync().ConfigureAwait(false);
                }
            }
        }
        var failure = new ZooKeeperConnectionException(
            tlsFailure.HasValue ? ZooKeeperConnectionFailure.Tls
                : addressTimedOut ? ZooKeeperConnectionFailure.Timeout
                : ZooKeeperConnectionFailure.Unreachable,
            endpoint,
            null,
            tlsFailure,
            tlsFailure.HasValue ? null : socketError
        );
        if (cancellationToken.IsCancellationRequested)
        {
            // The caller decides whether its token or the endpoint deadline ended the attempt.
            throw new OperationCanceledException(
                null,
                tlsFailure.HasValue ? failure : null,
                cancellationToken
            );
        }
        throw failure;
    }

    private async Task<ZooKeeperTlsCredentials> LoadCredentialsAsync(
        Func<CancellationToken, ValueTask<ZooKeeperTlsCredentials>> provider,
        ZooKeeperEndpoint endpoint,
        CancellationToken token
    )
    {
        token.ThrowIfCancellationRequested();
        var failure = new ZooKeeperConnectionException(
            ZooKeeperConnectionFailure.Tls,
            endpoint,
            null,
            ZooKeeperTlsFailureReason.ProviderFailed,
            null
        );
        if (Interlocked.Exchange(ref _providerBusy, 1) != 0)
        {
            throw failure;
        }
        // Bound outstanding application callbacks even when the provider ignores its token.
        var pending = Task.Run(async () => await provider(token).ConfigureAwait(false), token);
        bool detached = false;
        try
        {
            return await pending.WaitAsync(token).ConfigureAwait(false) ?? throw failure;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            detached = true;
            _ = DisposeLateCredentialsAsync(pending);
            throw new OperationCanceledException(null, failure, token);
        }
        catch (Exception)
        {
            throw failure;
        }
        finally
        {
            if (!detached)
            {
                Volatile.Write(ref _providerBusy, 0);
            }
        }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Design",
        "CA1031",
        Justification = "An abandoned application provider may fail after the deadline. Its exception must be observed without exposing provider details."
    )]
    private async Task DisposeLateCredentialsAsync(Task<ZooKeeperTlsCredentials> pending)
    {
        try
        {
            (await pending.ConfigureAwait(false))?.Dispose();
        }
        catch (Exception) { }
        finally
        {
            Volatile.Write(ref _providerBusy, 0);
        }
    }

    private static ZooKeeperTlsFailureReason ClassifyCertificate(
        SslPolicyErrors errors,
        X509Chain? chain
    )
    {
        if ((errors & SslPolicyErrors.RemoteCertificateNotAvailable) != 0)
        {
            return ZooKeeperTlsFailureReason.CertificateMissing;
        }
        if ((errors & SslPolicyErrors.RemoteCertificateNameMismatch) != 0)
        {
            return ZooKeeperTlsFailureReason.NameMismatch;
        }
        var flags = X509ChainStatusFlags.NoError;
        if (chain is not null)
        {
            foreach (var status in chain.ChainStatus)
            {
                flags |= status.Status;
            }
        }
        if ((flags & X509ChainStatusFlags.Revoked) != 0)
        {
            return ZooKeeperTlsFailureReason.Revoked;
        }
        if ((flags & X509ChainStatusFlags.NotTimeValid) != 0)
        {
            return ZooKeeperTlsFailureReason.InvalidValidityPeriod;
        }
        if (
            (
                flags
                & (
                    X509ChainStatusFlags.RevocationStatusUnknown
                    | X509ChainStatusFlags.OfflineRevocation
                )
            ) != 0
        )
        {
            return ZooKeeperTlsFailureReason.RevocationUnavailable;
        }
        return ZooKeeperTlsFailureReason.UntrustedChain;
    }

    private static bool IsUsable(IPAddress address) =>
        address.AddressFamily is AddressFamily.InterNetwork or AddressFamily.InterNetworkV6;
}
