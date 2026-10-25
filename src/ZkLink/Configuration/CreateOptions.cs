namespace ZkLink;

/// <summary>Immutable create settings with explicit ACLs and a discrete mode.</summary>
public sealed class CreateOptions
{
    /// <summary>Gets the maximum supported TTL in whole milliseconds.</summary>
    public const long MaxTtlMilliseconds = (1L << 40) - 1;

    /// <summary>Snapshots ACLs and validates TTL without admitting a network operation.</summary>
    public CreateOptions(
        IReadOnlyList<ZooKeeperAcl> acl,
        CreateMode mode = CreateMode.Persistent,
        TimeSpan? ttl = null
    )
    {
        if (mode < CreateMode.Persistent || mode > CreateMode.PersistentSequentialWithTtl)
            throw new ArgumentOutOfRangeException(nameof(mode));
        bool requiresTtl =
            mode is CreateMode.PersistentWithTtl or CreateMode.PersistentSequentialWithTtl;
        if (requiresTtl != ttl.HasValue)
            throw new ArgumentException("TTL is required only for TTL create modes.", nameof(ttl));
        if (
            ttl is { } value
            && (
                value.Ticks <= 0
                || value.Ticks % TimeSpan.TicksPerMillisecond != 0
                || value.Ticks / TimeSpan.TicksPerMillisecond > MaxTtlMilliseconds
            )
        )
            throw new ArgumentOutOfRangeException(
                nameof(ttl),
                "TTL must be positive whole milliseconds within the supported bound."
            );
        ArgumentNullException.ThrowIfNull(acl);
        if (acl.Count == 0)
            throw new ArgumentException(
                "Create requires at least one explicit ACL entry.",
                nameof(acl)
            );
        Acl = Snapshot.List(acl);
        Mode = mode;
        Ttl = ttl;
    }

    /// <summary>Gets the explicit ACL snapshot.</summary>
    public IReadOnlyList<ZooKeeperAcl> Acl { get; }

    /// <summary>Gets the discrete create mode.</summary>
    public CreateMode Mode { get; }

    /// <summary>Gets TTL for TTL modes, otherwise null; deletion timing is server-controlled.</summary>
    public TimeSpan? Ttl { get; }
}
