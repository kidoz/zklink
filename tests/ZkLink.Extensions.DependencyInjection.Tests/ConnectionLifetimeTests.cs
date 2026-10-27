using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NSubstitute;

namespace ZkLink.Extensions.DependencyInjection.Tests;

public sealed class ConnectionLifetimeTests
{
    [Fact]
    [Trait("Acceptance", "T40")]
    public async Task StartConnectsOnceWithOptionsAndCancellation()
    {
        var options = CreateOptions();
        var connection = Substitute.For<IZkLinkConnection>();
        var factory = Substitute.For<IZkLinkConnectionFactory>();
        factory.ConnectAsync(options, Arg.Any<CancellationToken>()).Returns(connection);
        var host = new ZkLinkConnectionHost(options, factory);
        using var cancellation = new CancellationTokenSource();

        await host.StartAsync(cancellation.Token);

        await factory.Received(1).ConnectAsync(options, cancellation.Token);
        Assert.Same(connection.Client, host.Client);
        await host.DisposeAsync();
    }

    [Fact]
    [Trait("Acceptance", "T40")]
    public async Task StopClosesAndMakesClientUnavailable()
    {
        var (host, connection, _) = CreateStartedHost(out var startTask);
        await startTask.ConfigureAwait(true);
        using var cancellation = new CancellationTokenSource();

        await host.StopAsync(cancellation.Token);

        await connection.Received(1).CloseAsync(cancellation.Token);
        Assert.Throws<InvalidOperationException>(() => host.Client);
        await host.DisposeAsync();
        await connection.Received(1).DisposeAsync();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Acceptance", "T40")]
    public async Task FailedOrCancelledStopStillDisposesTheConnection(bool cancelled)
    {
        var (host, connection, _) = CreateStartedHost(out var startTask);
        await startTask.ConfigureAwait(true);
        using var stop = new CancellationTokenSource();
        if (cancelled)
        {
            await stop.CancelAsync();
        }
        Exception error = cancelled
            ? new OperationCanceledException(stop.Token)
            : new IOException("close failed");
        connection.CloseAsync(stop.Token).Returns(Task.FromException(error));

        Assert.Same(
            error,
            await Assert.ThrowsAnyAsync<Exception>(() => host.StopAsync(stop.Token))
        );
        await connection.Received(1).DisposeAsync();
        await host.DisposeAsync();
        await connection.Received(1).DisposeAsync();
        Assert.Throws<InvalidOperationException>(() => host.Client);
    }

    [Fact]
    [Trait("Acceptance", "T40")]
    public async Task DisposeWithoutStopReleasesConnectionWithoutGracefulClose()
    {
        var (host, connection, _) = CreateStartedHost(out var startTask);
        await startTask.ConfigureAwait(true);

        await host.DisposeAsync();

        await connection.Received(1).DisposeAsync();
        await connection
            .DidNotReceiveWithAnyArgs()
            .CloseAsync(TestContext.Current.CancellationToken);
        Assert.Throws<InvalidOperationException>(() => host.Client);

        await host.StopAsync(TestContext.Current.CancellationToken);
        await host.DisposeAsync();
        await connection.Received(1).DisposeAsync();
    }

    [Fact]
    [Trait("Acceptance", "T40")]
    public async Task SecondStartDisposesAdditionalConnectionAndKeepsOriginal()
    {
        var options = CreateOptions();
        var first = Substitute.For<IZkLinkConnection>();
        var second = Substitute.For<IZkLinkConnection>();
        var factory = Substitute.For<IZkLinkConnectionFactory>();
        factory.ConnectAsync(options, Arg.Any<CancellationToken>()).Returns(first, second);
        var host = new ZkLinkConnectionHost(options, factory);
        await host.StartAsync(TestContext.Current.CancellationToken);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            host.StartAsync(TestContext.Current.CancellationToken)
        );

        Assert.Contains("already started", error.Message, StringComparison.Ordinal);
        await second.Received(1).DisposeAsync();
        Assert.Same(first.Client, host.Client);
        await host.DisposeAsync();
        await first.Received(1).DisposeAsync();
    }

    [Fact]
    [Trait("Acceptance", "T40")]
    public async Task StartupFailureDoesNotPublishClient()
    {
        var options = CreateOptions();
        var factory = Substitute.For<IZkLinkConnectionFactory>();
        factory
            .ConnectAsync(options, Arg.Any<CancellationToken>())
            .Returns(Task.FromException<IZkLinkConnection>(new IOException("connect failed")));
        var host = new ZkLinkConnectionHost(options, factory);

        await Assert.ThrowsAsync<IOException>(() =>
            host.StartAsync(TestContext.Current.CancellationToken)
        );

        Assert.Throws<InvalidOperationException>(() => host.Client);
        await host.DisposeAsync();
    }

    [Fact]
    [Trait("Acceptance", "T40")]
    public async Task RealHostStartsAndStopsRegisteredConnection()
    {
        var options = CreateOptions();
        var connection = Substitute.For<IZkLinkConnection>();
        var factory = Substitute.For<IZkLinkConnectionFactory>();
        factory.ConnectAsync(options, Arg.Any<CancellationToken>()).Returns(connection);
        using var host = new HostBuilder()
            .ConfigureServices(services =>
            {
                services.AddSingleton(factory);
                services.AddZkLink(options);
            })
            .Build();

        await host.StartAsync(TestContext.Current.CancellationToken);
        await host.StopAsync(TestContext.Current.CancellationToken);

        await factory.Received(1).ConnectAsync(options, Arg.Any<CancellationToken>());
        await connection.Received(1).CloseAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait("Acceptance", "T40")]
    public async Task ContainerDisposalBeforeStartDoesNotConnect()
    {
        var options = CreateOptions();
        var factory = Substitute.For<IZkLinkConnectionFactory>();
        var services = new ServiceCollection();
        services.AddSingleton(factory);
        services.AddZkLink(options);
        await using var provider = services.BuildServiceProvider();
        _ = provider.GetRequiredService<ZkLinkConnectionHost>();

        await provider.DisposeAsync();

        await factory
            .DidNotReceiveWithAnyArgs()
            .ConnectAsync(null!, TestContext.Current.CancellationToken);
    }

    private static (
        ZkLinkConnectionHost Host,
        IZkLinkConnection Connection,
        IZkLinkConnectionFactory Factory
    ) CreateStartedHost(out Task startTask)
    {
        var options = CreateOptions();
        var connection = Substitute.For<IZkLinkConnection>();
        var factory = Substitute.For<IZkLinkConnectionFactory>();
        factory.ConnectAsync(options, Arg.Any<CancellationToken>()).Returns(connection);
        var host = new ZkLinkConnectionHost(options, factory);
        startTask = host.StartAsync(TestContext.Current.CancellationToken);
        return (host, connection, factory);
    }

    private static ZooKeeperClientOptions CreateOptions() =>
        new() { ConnectionString = "localhost:2181" };
}
