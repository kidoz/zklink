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
        CancellationToken cancellationToken
    );
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
    private static readonly TimeSpan MinimumRetrievalTimeout = TimeSpan.FromMilliseconds(1);

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Reliability",
        "CA2000",
        Justification = "The socket transfers to NetworkStream, then SslStream, then TransportConnection; every untransferred local is disposed in finally."
    )]
    public async Task<TransportConnection> ConnectAsync(
        ZooKeeperEndpoint endpoint,
        ClientOptionsSnapshot options,
        CancellationToken cancellationToken
    )
    {
        var addresses = await resolver
            .ResolveAsync(endpoint.Host, cancellationToken)
            .ConfigureAwait(false);
        random.Shuffle(addresses);
        var tlsFailed = false;
        foreach (var address in addresses)
        {
            if (
                address.AddressFamily
                is not (AddressFamily.InterNetwork or AddressFamily.InterNetworkV6)
            )
            {
                continue;
            }
            Socket? socket = new(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp)
            {
                NoDelay = true,
            };
            NetworkStream? network = null;
            SslStream? ssl = null;
            try
            {
                await socket
                    .ConnectAsync(new IPEndPoint(address, endpoint.Port), cancellationToken)
                    .ConfigureAwait(false);
                network = new NetworkStream(socket, ownsSocket: true);
                socket = null;
                if (options.Settings.Tls.Enabled)
                {
                    ssl = new SslStream(network, leaveInnerStreamOpen: false);
                    network = null;
                    var tls = options.Settings.Tls;
                    var authentication = new SslClientAuthenticationOptions
                    {
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
                    if (tls.ClientCertificate is not null)
                    {
                        authentication.ClientCertificates = new X509CertificateCollection
                        {
                            tls.ClientCertificate,
                        };
                    }
                    if (tls.TrustRoots.Count != 0)
                    {
                        policy.TrustMode = X509ChainTrustMode.CustomRootTrust;
                        foreach (var root in tls.TrustRoots)
                        {
                            policy.CustomTrustStore.Add(root);
                        }
                    }
                    await ssl.AuthenticateAsClientAsync(authentication, cancellationToken)
                        .ConfigureAwait(false);
                }
                cancellationToken.ThrowIfCancellationRequested();
                var result = new TransportConnection(
                    (Stream?)ssl ?? network!,
                    endpoint,
                    options.Limits
                );
                ssl = null;
                network = null;
                return result;
            }
            catch (AuthenticationException)
            {
                tlsFailed = true;
            }
            catch (SocketException) { }
            catch (IOException)
            {
                tlsFailed |= options.Settings.Tls.Enabled;
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
            cancellationToken.ThrowIfCancellationRequested();
        }
        throw new ZooKeeperConnectionException(
            tlsFailed ? ZooKeeperConnectionFailure.Tls : ZooKeeperConnectionFailure.Unreachable,
            endpoint
        );
    }
}
