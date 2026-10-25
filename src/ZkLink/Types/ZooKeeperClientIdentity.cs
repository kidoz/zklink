namespace ZkLink;

/// <summary>An authenticated identity returned by whoAmI.</summary>
public sealed class ZooKeeperClientIdentity
{
    /// <summary>Creates an immutable scheme/user identity without formatting it in diagnostics.</summary>
    public ZooKeeperClientIdentity(string scheme, string user)
    {
        ArgumentException.ThrowIfNullOrEmpty(scheme);
        ArgumentNullException.ThrowIfNull(user);
        Scheme = scheme;
        User = user;
    }

    /// <summary>Gets the authentication scheme.</summary>
    public string Scheme { get; }

    /// <summary>Gets the server-reported user.</summary>
    public string User { get; }
}
