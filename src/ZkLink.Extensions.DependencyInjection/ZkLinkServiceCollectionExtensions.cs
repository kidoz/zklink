using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using ZkLink;
using ZkLink.Extensions.DependencyInjection;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Registers ZkLink with Microsoft.Extensions.DependencyInjection.</summary>
public static class ZkLinkServiceCollectionExtensions
{
    /// <summary>
    /// Registers a singleton <see cref="IZooKeeperClientAccessor"/> and an asynchronous host-owned connection lifetime.
    /// The accessor can be constructor-injected; its Client is available after ZkLink startup and before shutdown.
    /// </summary>
    /// <param name="services">The service collection being composed.</param>
    /// <param name="options">The client options captured when the host connects.</param>
    /// <returns>The same service collection.</returns>
    /// <exception cref="InvalidOperationException">ZkLink is already registered in this collection.</exception>
    public static IServiceCollection AddZkLink(
        this IServiceCollection services,
        ZooKeeperClientOptions options
    )
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);

        if (services.Any(static descriptor => descriptor.ServiceType == typeof(ZkLinkRegistration)))
        {
            throw new InvalidOperationException(
                "ZkLink is already registered in this service collection. Use an explicitly owned client for a separate coordination identity."
            );
        }

        services.AddSingleton(new ZkLinkRegistration());
        services.AddSingleton(options);
        services.TryAddSingleton<IZkLinkConnectionFactory, ZkLinkConnectionFactory>();
        services.AddSingleton<ZkLinkConnectionHost>();
        services.AddSingleton<IHostedService>(static provider =>
            provider.GetRequiredService<ZkLinkConnectionHost>()
        );
        services.AddSingleton<IZooKeeperClientAccessor, ZkLinkClientAccessor>();
        return services;
    }
}
