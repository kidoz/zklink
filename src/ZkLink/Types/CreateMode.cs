namespace ZkLink;

/// <summary>Specifies a discrete create mode; values are not combinable flags.</summary>
public enum CreateMode
{
    /// <summary>Persistent node.</summary>
    Persistent = 0,

    /// <summary>Session-owned ephemeral node.</summary>
    Ephemeral = 1,

    /// <summary>Persistent node with a server sequence suffix.</summary>
    PersistentSequential = 2,

    /// <summary>Ephemeral node with a server sequence suffix.</summary>
    EphemeralSequential = 3,

    /// <summary>Container parent eligible for server cleanup.</summary>
    Container = 4,

    /// <summary>Persistent node eligible for TTL cleanup.</summary>
    PersistentWithTtl = 5,

    /// <summary>Sequential persistent node eligible for TTL cleanup.</summary>
    PersistentSequentialWithTtl = 6,
}
