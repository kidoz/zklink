using System.Security.Cryptography.X509Certificates;

namespace ZkLink;

/// <summary>Independent certificate handles for a single TLS connection attempt. Returning this object from a provider transfers disposal to the client.</summary>
public sealed class ZooKeeperTlsCredentials : IDisposable
{
    internal X509Certificate2? ClientCertificate { get; }
    internal IReadOnlyList<X509Certificate2> TrustRoots { get; }

    /// <summary>Copies the supplied certificate handles. Original certificates remain application-owned.</summary>
    /// <exception cref="ArgumentException">The client certificate has no private key.</exception>
    /// <exception cref="ArgumentOutOfRangeException">More than 256 trust roots were supplied.</exception>
    public ZooKeeperTlsCredentials(
        X509Certificate2? clientCertificate = null,
        IReadOnlyList<X509Certificate2>? trustRoots = null
    )
    {
        if (clientCertificate is { HasPrivateKey: false })
        {
            throw new ArgumentException(
                "The mTLS certificate requires a private key.",
                nameof(clientCertificate)
            );
        }
        var count = trustRoots?.Count ?? 0;
        ArgumentOutOfRangeException.ThrowIfGreaterThan(count, 256);
        var copies = new List<X509Certificate2>(count);
        TrustRoots = copies;
        try
        {
            for (int i = 0; i < count; i++)
            {
                copies.Add(new X509Certificate2(trustRoots![i]));
            }
            ClientCertificate = clientCertificate is null
                ? null
                : new X509Certificate2(clientCertificate);
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    /// <summary>Releases this snapshot's certificate handles.</summary>
    public void Dispose()
    {
        ClientCertificate?.Dispose();
        foreach (var root in TrustRoots)
        {
            root.Dispose();
        }
    }
}
