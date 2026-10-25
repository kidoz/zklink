namespace ZkLink;

/// <summary>The disposition of one logical watch handle after local suppression.</summary>
public sealed class ZooKeeperWatchRemovalResult
{
    /// <summary>Creates an explicit removal result without conflating shared ownership with acknowledgment.</summary>
    public ZooKeeperWatchRemovalResult(long registrationId, WatchRemovalStatus status)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(registrationId, 1);
        if (!Enum.IsDefined(status))
        {
            throw new ArgumentOutOfRangeException(nameof(status));
        }
        RegistrationId = registrationId;
        Status = status;
    }

    /// <summary>Gets the stable positive logical registration ID.</summary>
    public long RegistrationId { get; }

    /// <summary>Gets the local/shared/server disposition.</summary>
    public WatchRemovalStatus Status { get; }
}
