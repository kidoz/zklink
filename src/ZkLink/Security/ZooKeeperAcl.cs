namespace ZkLink;

/// <summary>An immutable scheme-aware ACL entry; it does not configure client authentication.</summary>
public sealed class ZooKeeperAcl
{
    /// <summary>Creates an explicit ACL entry.</summary>
    public ZooKeeperAcl(ZooKeeperPermissions permissions, string scheme, string id)
    {
        if ((permissions & ~ZooKeeperPermissions.All) != 0)
            throw new ArgumentOutOfRangeException(nameof(permissions));
        ArgumentException.ThrowIfNullOrEmpty(scheme);
        ArgumentNullException.ThrowIfNull(id);
        Permissions = permissions;
        Scheme = scheme;
        Id = id;
    }

    /// <summary>Gets an explicit unsafe world:anyone entry granting all permissions.</summary>
    public static ZooKeeperAcl OpenUnsafe { get; } =
        new(ZooKeeperPermissions.All, "world", "anyone");

    /// <summary>Gets the permission bits.</summary>
    public ZooKeeperPermissions Permissions { get; }

    /// <summary>Gets the ACL scheme, independently of client authentication support.</summary>
    public string Scheme { get; }

    /// <summary>Gets the scheme-specific identity.</summary>
    public string Id { get; }
}
