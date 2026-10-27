using ZkLink.Diagnostics;
using ZkLink.Protocol;
using ZkLink.Requests;

namespace ZkLink.Watches;

internal interface IWatchSession
{
    internal TelemetryState Telemetry { get; }
    internal ZooKeeperSessionSnapshot Session { get; }
    internal void WakeWriter();
    internal Task<WireReply> SubmitWatchRemoval(
        WatchRequest request,
        IRequestObserver observer,
        CancellationToken token
    );
    internal bool TryWatchCleanup(
        WatchRequest request,
        IRequestObserver observer,
        out Task<WireReply>? task
    );
}
