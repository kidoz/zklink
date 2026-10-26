namespace ZkLink;

/// <summary>Identifies a logical one-shot watch, independently of server removal categories.</summary>
public enum ZooKeeperWatchMode
{
    /// <summary>A watch established by an existence read, including when the node is absent.</summary>
    OneShotExists = 0,

    /// <summary>A watch established by a successful data read.</summary>
    OneShotData = 1,

    /// <summary>A watch established by a successful child-list read.</summary>
    OneShotChildren = 2,
}
