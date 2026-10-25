namespace ZkLink;

/// <summary>Selects the required acknowledgment for watch removal.</summary>
public enum WatchRemovalMode
{
    /// <summary>Suppress delivery locally and perform best-effort server cleanup.</summary>
    Local = 0,

    /// <summary>Suppress locally and require a definitive server removal result.</summary>
    ServerConfirmed = 1,
}
