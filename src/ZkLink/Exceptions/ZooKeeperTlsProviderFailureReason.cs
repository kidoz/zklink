namespace ZkLink;

/// <summary>Safe failure details for an application TLS credentials provider.</summary>
public enum ZooKeeperTlsProviderFailureReason
{
    /// <summary>The provider threw an exception.</summary>
    Threw,

    /// <summary>The provider returned an empty credential snapshot reference.</summary>
    ReturnedNull,

    /// <summary>The attempt ended while waiting for credentials.</summary>
    Canceled,

    /// <summary>A previous provider invocation is still outstanding.</summary>
    AlreadyRunning,
}
