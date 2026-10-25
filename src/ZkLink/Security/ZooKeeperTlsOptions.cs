using System.Security.Cryptography.X509Certificates;

namespace ZkLink;

/// <summary>Explicit TLS policy without an accept-any-certificate fallback.</summary>
public sealed class ZooKeeperTlsOptions
{
    private ZooKeeperTlsOptions(
        bool enabled,
        X509RevocationMode revocationMode,
        X509Certificate2? clientCertificate,
        IReadOnlyList<X509Certificate2>? trustRoots,
        string? serverName
    )
    {
        if (!Enum.IsDefined(revocationMode))
        {
            throw new ArgumentOutOfRangeException(nameof(revocationMode));
        }
        if (serverName is not null)
        {
            _ = new ZooKeeperEndpoint(serverName);
        }
        if (trustRoots is { Count: > 256 })
        {
            throw new ArgumentOutOfRangeException(nameof(trustRoots));
        }
        Enabled = enabled;
        RevocationMode = revocationMode;
        ClientCertificate = clientCertificate;
        TrustRoots = trustRoots is null ? [] : Snapshot.List(trustRoots);
        ServerName = serverName;
    }

    /// <summary>Requires platform chain/name validation; supplied certificates remain application-owned.</summary>
    public static ZooKeeperTlsOptions Required(
        X509RevocationMode revocationMode = X509RevocationMode.Online,
        X509Certificate2? clientCertificate = null,
        IReadOnlyList<X509Certificate2>? trustRoots = null,
        string? serverName = null
    ) => new(true, revocationMode, clientCertificate, trustRoots, serverName);

    /// <summary>Explicitly selects plaintext, intended for controlled development fixtures.</summary>
    public static ZooKeeperTlsOptions Disabled() =>
        new(false, X509RevocationMode.Online, null, null, null);

    /// <summary>Gets whether TLS must succeed before the ZooKeeper handshake.</summary>
    public bool Enabled { get; }

    /// <summary>Gets the revocation policy.</summary>
    public X509RevocationMode RevocationMode { get; }

    /// <summary>Gets the application-owned mTLS certificate, if configured.</summary>
    public X509Certificate2? ClientCertificate { get; }

    /// <summary>Gets application-owned custom trust roots; an empty list uses platform trust.</summary>
    public IReadOnlyList<X509Certificate2> TrustRoots { get; }

    /// <summary>Gets an optional explicit TLS identity; otherwise the configured endpoint host is used.</summary>
    public string? ServerName { get; }
}
