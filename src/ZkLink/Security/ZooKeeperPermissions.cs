namespace ZkLink;

/// <summary>ZooKeeper ACL permission bits; child create/delete apply to the parent.</summary>
[Flags]
public enum ZooKeeperPermissions
{
    /// <summary>No permission bits.</summary>
    None = 0,

    /// <summary>Read data.</summary>
    Read = 1,

    /// <summary>Write data.</summary>
    Write = 2,

    /// <summary>Create children.</summary>
    Create = 4,

    /// <summary>Delete children.</summary>
    Delete = 8,

    /// <summary>Administer ACLs.</summary>
    Admin = 16,

    /// <summary>All five permission bits.</summary>
    All = Read | Write | Create | Delete | Admin,
}
