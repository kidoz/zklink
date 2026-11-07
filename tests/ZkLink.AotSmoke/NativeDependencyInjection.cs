using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ZkLink.Extensions.DependencyInjection;
using ZkLink.Samples.Basic;

namespace ZkLink.AotSmoke;

internal static class NativeDependencyInjection
{
    internal const string Success =
        "Native DI startup, constructor injection, shared scopes, borrowed-client disposal, live operations and acknowledged shutdown: verified.";

    internal static async Task VerifyAsync(
        NativeSettings settings,
        ZooKeeperClient observer,
        CancellationToken token
    )
    {
        using var authentication = settings.Authentication();
        var options = settings.Options(settings.Endpoint, [authentication]);
        var host = new HostBuilder()
            .ConfigureServices(services =>
            {
                services.AddZkLink(options);
                services.AddHostedService<Consumer>();
            })
            .Build();
        await using var hostLifetime = ((IAsyncDisposable)host).ConfigureAwait(false);
        var accessor = host.Services.GetRequiredService<IZooKeeperClientAccessor>();
        RequireUnavailable(accessor);
        await host.StartAsync(token).ConfigureAwait(false);
        var client = accessor.Client;
        var consumer = host.Services.GetServices<IHostedService>().OfType<Consumer>().Single();
        NativeSettings.Require(ReferenceEquals(client, consumer.StartedClient));
        for (int index = 0; index < 10_000; index++)
        {
            NativeSettings.Require(
                ReferenceEquals(
                    accessor,
                    host.Services.GetRequiredService<IZooKeeperClientAccessor>()
                )
            );
            NativeSettings.Require(ReferenceEquals(client, accessor.Client));
        }
        using (var scope = host.Services.CreateScope())
        {
            NativeSettings.Require(
                ReferenceEquals(
                    accessor,
                    scope.ServiceProvider.GetRequiredService<IZooKeeperClientAccessor>()
                )
            );
        }
        var asyncScope = host.Services.CreateAsyncScope();
        await using (asyncScope.ConfigureAwait(false))
        {
            NativeSettings.Require(
                ReferenceEquals(
                    accessor,
                    asyncScope.ServiceProvider.GetRequiredService<IZooKeeperClientAccessor>()
                )
            );
        }
        await client.DisposeAsync().ConfigureAwait(false);
        await client.SyncAsync("/", token).ConfigureAwait(false);
        await new NodeSample().RunAsync(client, token).ConfigureAwait(false);

        string ephemeral = SampleNodes.UniquePath("/zklink-native-di-");
        await client
            .CreateAsync(
                ephemeral,
                null,
                new CreateOptions(
                    [new ZooKeeperAcl(ZooKeeperPermissions.All, "auth", "")],
                    CreateMode.Ephemeral
                ),
                token
            )
            .ConfigureAwait(false);
        try
        {
            await host.StopAsync(token).ConfigureAwait(false);
            NativeSettings.Require(ReferenceEquals(client, consumer.StoppedClient));
            NativeSettings.Require(client.Session.CloseStatus == ZooKeeperCloseStatus.Acknowledged);
            RequireUnavailable(accessor);
            await observer.SyncAsync("/", token).ConfigureAwait(false);
            NativeSettings.Require(
                await observer.ExistsAsync(ephemeral, token).ConfigureAwait(false) is null
            );
        }
        finally
        {
            try
            {
                await observer
                    .DeleteAsync(ephemeral, ZNodeVersion.Any, token)
                    .ConfigureAwait(false);
            }
            catch (ZooKeeperException error)
                when (error.KnownErrorCode == ZooKeeperErrorCode.NoNode) { }
        }
        await Console.Out.WriteLineAsync(Success).ConfigureAwait(false);
    }

    private static void RequireUnavailable(IZooKeeperClientAccessor accessor)
    {
        try
        {
            _ = accessor.Client;
        }
        catch (InvalidOperationException)
        {
            return;
        }
        throw new InvalidOperationException(
            "Native DI exposed a client outside the host lifetime."
        );
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Performance",
        "CA1812",
        Justification = "The closed AddHostedService registration constructs this consumer; native startup verifies its constructor injection."
    )]
    private sealed class Consumer(IZooKeeperClientAccessor accessor) : IHostedService
    {
        internal ZooKeeperClient? StartedClient { get; private set; }
        internal ZooKeeperClient? StoppedClient { get; private set; }

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            StartedClient = accessor.Client;
            await StartedClient.SyncAsync("/", cancellationToken).ConfigureAwait(false);
        }

        public async Task StopAsync(CancellationToken cancellationToken)
        {
            StoppedClient = accessor.Client;
            await StoppedClient.SyncAsync("/", cancellationToken).ConfigureAwait(false);
        }
    }
}
