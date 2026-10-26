using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NSubstitute;

namespace ZkLink.Extensions.DependencyInjection.Tests;

public sealed class ServiceCollectionTests
{
    [Fact]
    public void AddZkLinkValidatesArguments()
    {
        Assert.Throws<ArgumentNullException>(() =>
            ZkLinkServiceCollectionExtensions.AddZkLink(null!, CreateOptions())
        );

        var services = new ServiceCollection();
        Assert.Throws<ArgumentNullException>(() => services.AddZkLink(null!));
    }

    [Fact]
    [Trait("Acceptance", "T40")]
    public void DuplicateRegistrationFailsWithoutAddingAnotherLifetime()
    {
        var services = new ServiceCollection();
        services.AddZkLink(CreateOptions());

        var error = Assert.Throws<InvalidOperationException>(() =>
            services.AddZkLink(CreateOptions())
        );

        Assert.Contains("already registered", error.Message, StringComparison.Ordinal);
        Assert.Single(
            services,
            descriptor => descriptor.ServiceType == typeof(ZkLinkConnectionHost)
        );
        Assert.Single(services, descriptor => descriptor.ServiceType == typeof(IHostedService));
    }

    [Fact]
    [Trait("Acceptance", "T40")]
    public async Task RegistrationSharesOptionsAndHostedLifetime()
    {
        var services = new ServiceCollection();
        var options = CreateOptions();
        var connectionFactory = Substitute.For<IZkLinkConnectionFactory>();
        services.AddSingleton(connectionFactory);
        services.AddZkLink(options);
        await using var provider = services.BuildServiceProvider();

        Assert.Same(options, provider.GetRequiredService<ZooKeeperClientOptions>());
        Assert.Same(connectionFactory, provider.GetRequiredService<IZkLinkConnectionFactory>());
        Assert.Same(
            provider.GetRequiredService<ZkLinkConnectionHost>(),
            Assert.Single(provider.GetServices<IHostedService>())
        );

        var error = Assert.Throws<InvalidOperationException>(() =>
            provider.GetRequiredService<ZooKeeperClient>()
        );
        Assert.Contains("after host startup", error.Message, StringComparison.Ordinal);
    }

    private static ZooKeeperClientOptions CreateOptions() =>
        new() { ConnectionString = "localhost:2181" };
}
