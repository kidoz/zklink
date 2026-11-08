using ZkLink.IntegrationTests;

namespace ZkLink.FaultTests;

// Only the owned isolated runner supplies this target: its verified ensemble leader.
// The workload still connects to the follower; maintenance retains leader write fences.
internal static class SoakFixture
{
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Reliability",
        "CA2000",
        Justification = "The returned LiveZooKeeperFixture owns the relay and disposes it after client cleanup, including failed initialization."
    )]
    internal static LiveZooKeeperFixture Create()
    {
        if (
            Environment.GetEnvironmentVariable("ZKLINK_SOAK_MAINTENANCE_ENDPOINT")
            is not { } endpoint
        )
            return new();
        var maintenance = new LoopbackFixtureRelay(endpoint, CancellationToken.None);
        return new(maintenance.Endpoint, maintenance);
    }
}
