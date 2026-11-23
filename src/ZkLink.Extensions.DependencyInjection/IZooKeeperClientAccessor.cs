namespace ZkLink.Extensions.DependencyInjection;

/// <summary>Provides access to the host-owned client without transferring session ownership.</summary>
/// <remarks>
/// Safe to constructor-inject before host startup. Register ZkLink before dependent hosted services
/// when startup is sequential, and access Client only after ZkLink has started.
/// Disposing the shared client does nothing; calling <see cref="ZooKeeperClient.CloseAsync"/>
/// on it closes the session for every consumer.
/// </remarks>
public interface IZooKeeperClientAccessor
{
    /// <summary>Gets the shared client after successful ZkLink startup and before shutdown.</summary>
    /// <exception cref="InvalidOperationException">ZkLink has not started or has shut down.</exception>
    ZooKeeperClient Client { get; }
}

internal sealed class ZkLinkClientAccessor(ZkLinkConnectionHost host) : IZooKeeperClientAccessor
{
    public ZooKeeperClient Client => host.Client;
}
