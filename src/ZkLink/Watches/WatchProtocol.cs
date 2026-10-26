namespace ZkLink.Watches;

internal static class WatchProtocol
{
    internal const int Any = 3;
    internal const int Data = 2;
    internal const int Children = 1;
    internal const int Persistent = 4;
    internal const int PersistentRecursive = 5;
    internal const int ResetXid = -8;
    internal const int NotificationXid = -1;
    internal const int SessionEvent = -1;
    internal const int ServerConnected = 3;
    internal const int ServerDisconnected = 0;
    internal const int ServerAuthFailed = 4;
    internal const int Success = 0;
    internal const int RestoreTargetBytes = 128 * 1024;
    internal const int RestoreFrameOverhead = 28;
    internal const int ExtendedRestoreFrameOverhead = 36;
}
