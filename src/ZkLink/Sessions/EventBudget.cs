using ZkLink.Diagnostics;

namespace ZkLink.Sessions;

// Shared by state and watch queues; all access is serialized by the session gate.
internal sealed class EventBudget(object gate, int limit, TelemetryState? telemetry = null)
{
    private int _used;
    internal int Limit { get; } = limit;

    internal bool Reserve(int count)
    {
        SessionGate.RequireHeld(gate);
        if (count > Limit - _used)
            return false;
        _used += count;
        telemetry?.Backlog(_used);
        return true;
    }

    internal void Release(int count)
    {
        SessionGate.RequireHeld(gate);
        _used -= count;
        telemetry?.Backlog(_used);
    }
}
