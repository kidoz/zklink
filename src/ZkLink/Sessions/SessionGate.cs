using System.Diagnostics;

namespace ZkLink.Sessions;

internal static class SessionGate
{
    [Conditional("DEBUG")]
    internal static void RequireHeld(object gate)
    {
        Debug.Assert(
            Monitor.IsEntered(gate),
            "The current thread must hold the shared session gate."
        );
    }
}
