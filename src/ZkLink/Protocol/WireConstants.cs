namespace ZkLink.Protocol;

// Control operations have no public application API.
internal static class ControlOpcode
{
    internal const int Ping = 11;
    internal const int Auth = 100;
    internal const int SetWatches = 101;
    internal const int SetWatches2 = 105;
}

internal static class Xids
{
    internal const int Notification = -1;
    internal const int Ping = -2;
    internal const int Auth = -4;
    internal const int SetWatches = -8;

    // Positive application IDs are assigned in send order after measuring the request.
    internal static int ForMeasurement(int opcode) =>
        opcode switch
        {
            ControlOpcode.Ping => Ping,
            ControlOpcode.Auth => Auth,
            ControlOpcode.SetWatches or ControlOpcode.SetWatches2 => SetWatches,
            _ => 1,
        };
}

// ZooKeeper KeeperState values in a watch notification.
internal enum KeeperState
{
    Expired = -112,
    Unknown = -1,
    Disconnected = 0,
    NoSyncConnected = 1,
    SyncConnected = 3,
    AuthFailed = 4,
    ConnectedReadOnly = 5,
    SaslAuthenticated = 6,
    Closed = 7,
}

internal static class MultiHeader
{
    // The done bit distinguishes the terminator from an ErrorResult with the same opcode.
    internal const int ErrorOrEnd = -1;
    internal const int NoError = -1;
}

internal static class CreateFlags
{
    internal static bool IsSequential(int flags) =>
        (CreateMode)flags
            is CreateMode.PersistentSequential
                or CreateMode.EphemeralSequential
                or CreateMode.PersistentSequentialWithTtl;
}
