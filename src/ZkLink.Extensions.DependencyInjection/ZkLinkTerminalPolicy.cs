namespace ZkLink.Extensions.DependencyInjection;

/// <summary>Controls how the host responds to a terminal ZooKeeper session.</summary>
public enum ZkLinkTerminalPolicy
{
    /// <summary>Keep the terminal session visible to callers and health checks.</summary>
    Fail,

    /// <summary>Request graceful host shutdown.</summary>
    StopApplication,

    /// <summary>Create a new session. Ephemerals and watches belong to the previous session.</summary>
    Recreate,
}
