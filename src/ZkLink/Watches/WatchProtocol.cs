namespace ZkLink.Watches;

internal static class WatchProtocol
{
    internal const int Data = 2;
    internal const int Children = 1;
    internal const int ResetXid = -8;
    internal const int NotificationXid = -1;
    internal const int SessionEvent = -1;
    internal const int ServerConnected = 3;
    internal const int Success = 0;
    internal const int RestoreTargetBytes = 128 * 1024;
    internal const int RestoreFrameOverhead = 28;
}
