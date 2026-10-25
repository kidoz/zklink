namespace ZkLink;

/// <summary>An operator assertion about the least capable configured endpoint, not detected metadata.</summary>
public enum ZooKeeperCompatibilityProfile
{
    /// <summary>The common ZooKeeper 3.8 feature profile.</summary>
    ZooKeeper38 = 0,

    /// <summary>The ZooKeeper 3.9 feature profile.</summary>
    ZooKeeper39 = 1,
}
