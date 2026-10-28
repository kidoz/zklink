using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
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

    [Fact(Timeout = 10000)]
    [Trait("Acceptance", "T40")]
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Reliability",
        "CA2025",
        Justification = "The server task is cancelled and awaited in finally before the listener and cancellation source are disposed."
    )]
    public async Task ContainerChecksLifetimeOnEveryResolutionAndSharesTheStartedClient()
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken
        );
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var server = ServeSessionAsync(listener, stop.Token);
        try
        {
            var services = new ServiceCollection();
            services.AddZkLink(
                new ZooKeeperClientOptions
                {
                    ConnectionString = "127.0.0.1:" + ((IPEndPoint)listener.LocalEndpoint).Port,
                    Tls = ZooKeeperTlsOptions.Disabled(),
                }
            );
            await using var provider = services.BuildServiceProvider();
            Assert.Throws<InvalidOperationException>(() =>
                provider.GetRequiredService<ZooKeeperClient>()
            );
            var lifetime = provider.GetRequiredService<ZkLinkConnectionHost>();
            await lifetime.StartAsync(stop.Token);
            var client = provider.GetRequiredService<ZooKeeperClient>();
            Assert.Same(client, provider.GetRequiredService<ZooKeeperClient>());
            await using (var scope = provider.CreateAsyncScope())
            {
                Assert.Same(client, scope.ServiceProvider.GetRequiredService<ZooKeeperClient>());
            }

            Assert.NotEqual(ZooKeeperSessionState.Closed, client.Session.State);
            Assert.Same(client, provider.GetRequiredService<ZooKeeperClient>());
            await lifetime.StopAsync(stop.Token);
            Assert.Equal(ZooKeeperCloseStatus.Acknowledged, client.Session.CloseStatus);
            Assert.Throws<InvalidOperationException>(() =>
                provider.GetRequiredService<ZooKeeperClient>()
            );
            await server.ConfigureAwait(true);
        }
        finally
        {
            await stop.CancelAsync();
            await server.ConfigureAwait(true);
        }
    }

    private static async Task ServeSessionAsync(
        TcpListener listener,
        CancellationToken cancellationToken
    )
    {
        try
        {
            using var socket = await listener
                .AcceptTcpClientAsync(cancellationToken)
                .ConfigureAwait(false);
            var stream = socket.GetStream();
            await using var streamLifetime = stream.ConfigureAwait(false);
            _ = await ReadFrameAsync(stream, cancellationToken).ConfigureAwait(false);
            // ConnectResponse: version, timeout, session ID, 16-byte password, read-only flag.
            var handshake = new byte[41];
            BinaryPrimitives.WriteInt32BigEndian(handshake, 37);
            BinaryPrimitives.WriteInt32BigEndian(handshake.AsSpan(8), 3000);
            BinaryPrimitives.WriteInt64BigEndian(handshake.AsSpan(12), 123);
            BinaryPrimitives.WriteInt32BigEndian(handshake.AsSpan(20), 16);
            await stream.WriteAsync(handshake, cancellationToken).ConfigureAwait(false);
            while (true)
            {
                var request = await ReadFrameAsync(stream, cancellationToken).ConfigureAwait(false);
                var xid = BinaryPrimitives.ReadInt32BigEndian(request);
                var opcode = BinaryPrimitives.ReadInt32BigEndian(request.AsSpan(4));
                Assert.True(opcode is 11 or -11);
                var reply = new byte[20];
                BinaryPrimitives.WriteInt32BigEndian(reply, 16);
                BinaryPrimitives.WriteInt32BigEndian(reply.AsSpan(4), xid);
                await stream.WriteAsync(reply, cancellationToken).ConfigureAwait(false);
                if (opcode == -11)
                {
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    private static async Task<byte[]> ReadFrameAsync(
        Stream stream,
        CancellationToken cancellationToken
    )
    {
        var prefix = new byte[4];
        await stream.ReadExactlyAsync(prefix, cancellationToken).ConfigureAwait(false);
        var length = BinaryPrimitives.ReadInt32BigEndian(prefix);
        Assert.InRange(length, 8, 1024);
        var payload = new byte[length];
        await stream.ReadExactlyAsync(payload, cancellationToken).ConfigureAwait(false);
        return payload;
    }

    private static ZooKeeperClientOptions CreateOptions() =>
        new() { ConnectionString = "localhost:2181" };
}
