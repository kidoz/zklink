namespace ZkLink;

/// <summary>A server node invalidation or watch-removal notification.</summary>
public enum ZooKeeperWatchEventKind
{
    /// <summary>No node event is specified; this value is not emitted by watch streams.</summary>
    None = 0,

    /// <summary>The node was created.</summary>
    NodeCreated = 1,

    /// <summary>The node was deleted.</summary>
    NodeDeleted = 2,

    /// <summary>The node's data changed.</summary>
    NodeDataChanged = 3,

    /// <summary>The node's child list changed.</summary>
    NodeChildrenChanged = 4,

    /// <summary>The server removed a data or existence watch.</summary>
    DataWatchRemoved = 5,

    /// <summary>The server removed a child watch.</summary>
    ChildWatchRemoved = 6,
}

/// <summary>An immutable invalidation, not a complete change history; default formatting omits its path.</summary>
public sealed class ZooKeeperWatchEvent
{
    internal ZooKeeperWatchEvent(
        long registrationId,
        ZooKeeperWatchEventKind kind,
        string path,
        long? zxid
    )
    {
        RegistrationId = registrationId;
        Kind = kind;
        Path = path;
        Zxid = zxid;
    }

    /// <summary>Gets the logical registration that received this event.</summary>
    public long RegistrationId { get; }

    /// <summary>Gets the server-reported event kind.</summary>
    public ZooKeeperWatchEventKind Kind { get; }

    /// <summary>Gets the client-relative path.</summary>
    public string Path { get; }

    /// <summary>Gets the optional server zxid; null means unavailable and notifications never advance the restore watermark.</summary>
    public long? Zxid { get; }
}
