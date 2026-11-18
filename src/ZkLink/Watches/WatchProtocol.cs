using ZkLink.Protocol;

namespace ZkLink.Watches;

internal static class WatchProtocol
{
    internal const int Any = 3;
    internal const int Data = 2;
    internal const int Children = 1;
    internal const int Persistent = 4;
    internal const int PersistentRecursive = 5;
    internal const int ResetXid = Xids.SetWatches;
    internal const int NotificationXid = Xids.Notification;
    internal const int SessionEvent = -1;
    internal const int ServerExpired = (int)KeeperState.Expired;
    internal const int ServerConnected = (int)KeeperState.SyncConnected;
    internal const int ServerDisconnected = (int)KeeperState.Disconnected;
    internal const int ServerAuthFailed = (int)KeeperState.AuthFailed;
    internal const int Success = 0;
    internal const int RestoreTargetBytes = 128 * 1024;
    internal const int RestoreFrameOverhead = 28;
    internal const int ExtendedRestoreFrameOverhead = 36;
}
