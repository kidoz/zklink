namespace ZkLink;

/// <summary>Safe TLS failure categories reported by the platform or credential provider.</summary>
public enum ZooKeeperTlsFailureReason
{
    /// <summary>The TLS handshake failed.</summary>
    HandshakeFailed,

    /// <summary>The credential provider failed or exceeded its deadline.</summary>
    ProviderFailed,

    /// <summary>The certificate name did not match the target.</summary>
    NameMismatch,

    /// <summary>The certificate chain was not trusted.</summary>
    UntrustedChain,

    /// <summary>A certificate was outside its validity period.</summary>
    InvalidValidityPeriod,

    /// <summary>The platform could not determine revocation status.</summary>
    RevocationUnavailable,

    /// <summary>A certificate was revoked.</summary>
    Revoked,

    /// <summary>The peer did not supply a certificate.</summary>
    CertificateMissing,
}
