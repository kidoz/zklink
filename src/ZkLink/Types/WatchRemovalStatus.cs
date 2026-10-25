namespace ZkLink;

/// <summary>Describes removal of a logical handle and its server registration.</summary>
public enum WatchRemovalStatus
{
    /// <summary>Local delivery is suppressed; server removal is unconfirmed.</summary>
    LocalOnly = 0,

    /// <summary>The server acknowledged removal.</summary>
    ServerConfirmed = 1,

    /// <summary>The server definitively reported NOWATCHER.</summary>
    NoWatcher = 2,

    /// <summary>Local ownership ended; another handle still requires the server registration.</summary>
    SharedRegistrationRetained = 3,
}
