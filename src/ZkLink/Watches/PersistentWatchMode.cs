namespace ZkLink;

/// <summary>Selects the scope of a watch that remains active after each invalidation.</summary>
public enum PersistentWatchMode
{
    /// <summary>Observes node and child-list changes at the exact watched path.</summary>
    Persistent = 0,

    /// <summary>Observes node changes at the path and descendants, without child-list duplicates.</summary>
    PersistentRecursive = 1,
}
