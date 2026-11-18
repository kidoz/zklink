using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace ZkLink.Extensions.DependencyInjection;

/// <summary>Reports host-owned session readiness without issuing a ZooKeeper request.</summary>
public sealed class ZkLinkHealthCheck(IZooKeeperClientAccessor accessor) : IHealthCheck
{
    /// <inheritdoc />
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            return Task.FromResult(ForState(accessor.Client.Session.State));
        }
        catch (InvalidOperationException)
        {
            return Task.FromResult(
                HealthCheckResult.Unhealthy("No host-owned session is available.")
            );
        }
    }

    internal static HealthCheckResult ForState(ZooKeeperSessionState state) =>
        state switch
        {
            ZooKeeperSessionState.Connected => HealthCheckResult.Healthy(
                nameof(ZooKeeperSessionState.Connected)
            ),
            ZooKeeperSessionState.Connecting
            or ZooKeeperSessionState.Disconnected
            or ZooKeeperSessionState.Authenticating
            or ZooKeeperSessionState.Restoring => HealthCheckResult.Degraded(state.ToString()),
            _ => HealthCheckResult.Unhealthy(state.ToString()),
        };
}
