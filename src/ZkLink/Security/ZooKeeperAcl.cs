namespace ZkLink;

/// <summary>An immutable scheme-aware ACL entry; it does not configure client authentication.</summary>
public sealed class ZooKeeperAcl
{
    /// <summary>Creates an explicit ACL entry.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="permissions"/> has bits outside the five defined permissions.</exception>
    /// <exception cref="ArgumentException"><paramref name="scheme"/> is empty or <paramref name="id"/> is null.</exception>
    public ZooKeeperAcl(ZooKeeperPermissions permissions, string scheme, string id)
    {
        if ((permissions & ~ZooKeeperPermissions.All) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(permissions));
        }
        ArgumentException.ThrowIfNullOrEmpty(scheme);
        ArgumentNullException.ThrowIfNull(id);
        Permissions = permissions;
        Scheme = scheme;
        Id = id;
    }

    /// <summary>Gets an explicit unsafe <c>world:anyone</c> entry that grants read, write, create, delete and admin permissions to every client, including unauthenticated ones.</summary>
    /// <remarks>Use it only for nodes that are intentionally public. It is never applied by default; every create requires an explicit ACL.</remarks>
    public static ZooKeeperAcl OpenUnsafe { get; } =
        new(ZooKeeperPermissions.All, "world", "anyone");

    /// <summary>Gets the permission bits.</summary>
    public ZooKeeperPermissions Permissions { get; }

    /// <summary>Gets the ACL scheme, independently of client authentication support.</summary>
    public string Scheme { get; }

    /// <summary>Gets the scheme-specific identity.</summary>
    public string Id { get; }
}
