using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using ZkLink;
using ZkLink.Extensions.DependencyInjection;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Registers ZkLink with Microsoft.Extensions.DependencyInjection.</summary>
public static class ZkLinkServiceCollectionExtensions
{
    /// <summary>
    /// Registers one host-owned <see cref="ZooKeeperClient"/> and its asynchronous connection lifetime.
    /// The client can be resolved after host startup and before shutdown.
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
        services.AddSingleton(static provider =>
            provider.GetRequiredService<ZkLinkConnectionHost>().Client
        );
        return services;
    }
}
