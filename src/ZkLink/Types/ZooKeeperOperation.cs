namespace ZkLink;

/// <summary>Identifies supported application operations by their wire opcode.</summary>
public enum ZooKeeperOperation
{
    /// <summary>Legacy create.</summary>
    Create = 1,

    /// <summary>Delete a node.</summary>
    Delete = 2,

    /// <summary>Read existence and stat.</summary>
    Exists = 3,

    /// <summary>Read data and stat.</summary>
    GetData = 4,

    /// <summary>Write data.</summary>
    SetData = 5,

    /// <summary>Read ACL entries.</summary>
    GetAcl = 6,

    /// <summary>Write ACL entries.</summary>
    SetAcl = 7,

    /// <summary>Read child names without parent Stat inside multi-read.</summary>
    GetChildrenNames = 8,

    /// <summary>Read children and stat.</summary>
    GetChildren = 12,

    /// <summary>Synchronize a path.</summary>
    Sync = 9,

    /// <summary>Check a version inside multi.</summary>
    Check = 13,

    /// <summary>Atomic write multi.</summary>
    Multi = 14,

    /// <summary>Create with returned stat.</summary>
    CreateWithStat = 15,

    /// <summary>Create a container node.</summary>
    CreateContainer = 19,

    /// <summary>Create a TTL node.</summary>
    CreateTtl = 21,

    /// <summary>Read multi without snapshot guarantees.</summary>
    MultiRead = 22,

    /// <summary>List session ephemerals.</summary>
    GetEphemerals = 103,

    /// <summary>Count descendants.</summary>
    GetAllChildrenNumber = 104,

    /// <summary>Add a persistent watch.</summary>
    AddWatch = 106,

    /// <summary>Remove a server watch.</summary>
    RemoveWatches = 18,

    /// <summary>Read authenticated identities.</summary>
    WhoAmI = 107,

    /// <summary>Close the session.</summary>
    CloseSession = -11,
}
