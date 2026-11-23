namespace ZkLink;

/// <summary>A node invalidation, watch-removal notification, or local reconnect gap.</summary>
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

    /// <summary>Reserved for a removed data or existence watch. The supported ZooKeeper 3.8.7 and 3.9.6 servers do not send this event.</summary>
    DataWatchRemoved = 5,

    /// <summary>Reserved for a removed child watch. The supported ZooKeeper 3.8.7 and 3.9.6 servers do not send this event.</summary>
    ChildWatchRemoved = 6,

    /// <summary>Reserved for a removed persistent watch. The supported ZooKeeper 3.8.7 and 3.9.6 servers do not send this event; server-confirmed removal is reported by <see cref="ZooKeeperWatch.RemoveAsync"/>.</summary>
    PersistentWatchRemoved = 7,

    /// <summary>The session reconnected; reread or resnapshot because missed changes cannot be reconstructed.</summary>
    ReconnectedWithGap = 8,
}

/// <summary>An immutable invalidation, not a complete change history; default formatting omits its path.</summary>
public sealed class ZooKeeperWatchEvent
{
    internal ZooKeeperWatchEvent(
        long registrationId,
        ZooKeeperWatchEventKind kind,
        string path,
        long? zxid,
        long connectionGeneration
    )
    {
        RegistrationId = registrationId;
        Kind = kind;
        Path = path;
        Zxid = zxid;
        ConnectionGeneration = connectionGeneration;
    }

    /// <summary>Gets the logical registration that received this event.</summary>
    public long RegistrationId { get; }

    /// <summary>Gets the invalidation kind; ReconnectedWithGap is generated locally.</summary>
    public ZooKeeperWatchEventKind Kind { get; }

    /// <summary>Gets the client-relative path.</summary>
    public string Path { get; }

    /// <summary>Gets the optional server zxid; null means unavailable and notifications never advance the restore watermark.</summary>
    public long? Zxid { get; }

    /// <summary>Gets the connection generation on which this event was received or the gap was published.</summary>
    public long ConnectionGeneration { get; }
}
