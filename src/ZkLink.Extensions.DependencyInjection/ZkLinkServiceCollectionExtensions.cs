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

        return services.AddZkLink(_ => options);
    }

    /// <summary>Creates private client options at host startup and for each explicitly requested replacement.</summary>
    public static IServiceCollection AddZkLink(
        this IServiceCollection services,
        Func<IServiceProvider, ZooKeeperClientOptions> optionsFactory,
        ZkLinkTerminalPolicy terminalPolicy = ZkLinkTerminalPolicy.Fail
    )
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(optionsFactory);
        if (!Enum.IsDefined(terminalPolicy))
        {
            throw new ArgumentOutOfRangeException(nameof(terminalPolicy));
        }
        if (services.Any(static descriptor => descriptor.ServiceType == typeof(ZkLinkRegistration)))
        {
            throw new InvalidOperationException(
                "ZkLink is already registered in this service collection. Use an explicitly owned client for a separate coordination identity."
            );
        }

        services.AddSingleton(new ZkLinkRegistration());
        services.TryAddSingleton<ZkLinkHealthCheck>();
        services.TryAddSingleton<IZkLinkConnectionFactory>(
            static _ => new ZkLinkConnectionFactory()
        );
        services.AddSingleton(provider => new ZkLinkConnectionHost(
            () =>
                optionsFactory(provider)
                ?? throw new InvalidOperationException("The ZkLink options factory returned null."),
            provider.GetRequiredService<IZkLinkConnectionFactory>(),
            terminalPolicy,
            terminalPolicy == ZkLinkTerminalPolicy.Fail
                ? null
                : provider.GetRequiredService<IHostApplicationLifetime>()
        ));
        services.AddSingleton<IHostedService>(static provider =>
            provider.GetRequiredService<ZkLinkConnectionHost>()
        );
        services.AddSingleton<IZooKeeperClientAccessor>(static provider => new ZkLinkClientAccessor(
            provider.GetRequiredService<ZkLinkConnectionHost>()
        ));
        return services;
    }
}
