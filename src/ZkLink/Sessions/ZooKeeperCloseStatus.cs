namespace ZkLink;

/// <summary>Server evidence associated with local close.</summary>
public enum ZooKeeperCloseStatus
{
    /// <summary>No close has completed.</summary>
    None = 0,

    /// <summary>The server acknowledged closeSession.</summary>
    Acknowledged = 1,

    /// <summary>Only local cleanup is known; ephemerals may persist.</summary>
    Unacknowledged = 2,

    /// <summary>Server-confirmed expiry made closeSession unnecessary.</summary>
    SessionExpired = 3,
}
