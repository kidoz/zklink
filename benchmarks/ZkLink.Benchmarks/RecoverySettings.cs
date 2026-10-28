using System.Globalization;

namespace ZkLink.Benchmarks;

internal sealed record RecoverySettings(LiveSettings Common, int Watches)
{
    internal const int SmokeWatches = 100;
    internal const int FullWatches = 10_000;
    internal const int BatchSize = 32;
    internal const int PathPadding = 32;
    internal const int CaseGuardSeconds = 240;
    internal const int ProcessGuardSeconds = 300;
    internal static readonly TimeSpan RecoveryGuard = TimeSpan.FromSeconds(15);
    internal static readonly TimeSpan CleanupGuard = TimeSpan.FromSeconds(30);

    internal static RecoverySettings Read(string count, string output)
    {
        int watches = int.Parse(count, NumberStyles.None, CultureInfo.InvariantCulture);
        if (watches is not (SmokeWatches or FullWatches))
        {
            throw new InvalidOperationException("Recovery workload must use 100 or 10000 watches.");
        }
        return new(
            LiveSettings.Read(
                "1",
                "get",
                CodecWorkload.SmallPayloadBytes.ToString(CultureInfo.InvariantCulture),
                "1",
                output
            ),
            watches
        );
    }
}
