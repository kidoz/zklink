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
        var host = new ZkLinkConnectionHost(() => options, factory);
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
    public async Task SecondStartFailsWithoutConnectingAgainAndKeepsOriginal()
    {
        var options = CreateOptions();
        var connection = Substitute.For<IZkLinkConnection>();
        var factory = Substitute.For<IZkLinkConnectionFactory>();
        factory.ConnectAsync(options, Arg.Any<CancellationToken>()).Returns(connection);
        var host = new ZkLinkConnectionHost(() => options, factory);
        await host.StartAsync(TestContext.Current.CancellationToken);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            host.StartAsync(TestContext.Current.CancellationToken)
        );

        Assert.Contains("already started", error.Message, StringComparison.Ordinal);
        await factory.Received(1).ConnectAsync(options, Arg.Any<CancellationToken>());
        Assert.Same(connection.Client, host.Client);
        await host.DisposeAsync();
        await connection.Received(1).DisposeAsync();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Acceptance", "T40")]
    public async Task ShutdownDuringStartupWaitsAndReleasesConnection(bool dispose)
    {
        var options = CreateOptions();
        var connection = Substitute.For<IZkLinkConnection>();
        var factory = Substitute.For<IZkLinkConnectionFactory>();
        var pendingConnection = new TaskCompletionSource<IZkLinkConnection>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        factory.ConnectAsync(options, Arg.Any<CancellationToken>()).Returns(pendingConnection.Task);
        var host = new ZkLinkConnectionHost(() => options, factory);
        var start = host.StartAsync(TestContext.Current.CancellationToken);

        var shutdown = dispose
            ? host.DisposeAsync().AsTask()
            : host.StopAsync(TestContext.Current.CancellationToken);
        Assert.False(shutdown.IsCompleted);

        pendingConnection.SetResult(connection);
        await start.ConfigureAwait(true);
        await shutdown.ConfigureAwait(true);

        await connection.Received(1).DisposeAsync();
        Assert.Throws<InvalidOperationException>(() => host.Client);
        await host.DisposeAsync();
        await connection.Received(1).DisposeAsync();
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
        var host = new ZkLinkConnectionHost(() => options, factory);

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

    [Theory(Timeout = 10000)]
    [InlineData(ZkLinkTerminalPolicy.StopApplication)]
    [InlineData(ZkLinkTerminalPolicy.Recreate)]
    public async Task TerminalPolicyObservesOnlyCompletedSessions(ZkLinkTerminalPolicy policy)
    {
        var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var observed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var replacementEnd = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var first = Substitute.For<IZkLinkConnection>();
        first.Completion.Returns(ended.Task);
        first.State.Returns(ZooKeeperSessionState.Expired);
        var replacement = Substitute.For<IZkLinkConnection>();
        replacement.Completion.Returns(_ =>
        {
            observed.TrySetResult();
            return replacementEnd.Task;
        });
        var application = Substitute.For<IHostApplicationLifetime>();
        application.When(a => a.StopApplication()).Do(_ => observed.TrySetResult());
        var factory = Substitute.For<IZkLinkConnectionFactory>();
        var options = CreateOptions();
        factory.ConnectAsync(options, Arg.Any<CancellationToken>()).Returns(first, replacement);
        int captures = 0;
        var host = new ZkLinkConnectionHost(
            () =>
            {
                captures++;
                return options;
            },
            factory,
            policy,
            application
        );
        await host.StartAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1, captures);
        ended.SetResult();
        await observed.Task.WaitAsync(TestContext.Current.CancellationToken);
        Assert.Equal(policy == ZkLinkTerminalPolicy.Recreate ? 2 : 1, captures);
        await host.StopAsync(TestContext.Current.CancellationToken);
        await host.DisposeAsync();
        if (policy == ZkLinkTerminalPolicy.Recreate)
        {
            await replacement.Received(1).DisposeAsync();
            application.DidNotReceive().StopApplication();
        }
        else
        {
            application.Received(1).StopApplication();
        }
    }

    [Theory]
    [InlineData(
        ZooKeeperSessionState.Connected,
        Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Healthy
    )]
    [InlineData(
        ZooKeeperSessionState.Disconnected,
        Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Degraded
    )]
    [InlineData(
        ZooKeeperSessionState.Expired,
        Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Unhealthy
    )]
    public void HealthReflectsSessionState(
        ZooKeeperSessionState state,
        Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus status
    ) => Assert.Equal(status, ZkLinkHealthCheck.ForState(state).Status);

    [Theory(Timeout = 10000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ShutdownDuringReplacementRetiresTheLateConnectionWithoutPublishingIt(
        bool dispose
    )
    {
        var token = TestContext.Current.CancellationToken;
        var first = Substitute.For<IZkLinkConnection>();
        var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        first.Completion.Returns(ended.Task);
        first.State.Returns(ZooKeeperSessionState.Lost);
        var next = Substitute.For<IZkLinkConnection>();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var connect = new TaskCompletionSource<IZkLinkConnection>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var factory = Substitute.For<IZkLinkConnectionFactory>();
        var options = CreateOptions();
        factory
            .ConnectAsync(options, Arg.Any<CancellationToken>())
            .Returns(
                _ => Task.FromResult(first),
                _ =>
                {
                    started.TrySetResult();
                    return connect.Task;
                }
            );
        var application = Substitute.For<IHostApplicationLifetime>();
        var host = new ZkLinkConnectionHost(
            () => options,
            factory,
            ZkLinkTerminalPolicy.Recreate,
            application
        );
        await host.StartAsync(token);
        ended.SetResult();
        await started.Task.WaitAsync(token);

        var shutdown = dispose ? host.DisposeAsync().AsTask() : host.StopAsync(token);
        try
        {
            Assert.False(shutdown.IsCompleted);
            Assert.Throws<InvalidOperationException>(() => host.Client);
        }
        finally
        {
            connect.SetResult(next);
            await shutdown.ConfigureAwait(true);
            await host.DisposeAsync();
        }
        await first.Received(1).DisposeAsync();
        await next.Received(1).DisposeAsync();
        await next.DidNotReceiveWithAnyArgs().CloseAsync(token);
        application.DidNotReceive().StopApplication();
        Assert.Throws<InvalidOperationException>(() => host.Client);
    }

    [Theory(Timeout = 10000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedReplacementRequestsHostShutdownOnceAndRetainsTheOriginalForCleanup(
        bool factoryThrows
    )
    {
        var token = TestContext.Current.CancellationToken;
        var first = Substitute.For<IZkLinkConnection>();
        var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        first.Completion.Returns(ended.Task);
        first.State.Returns(ZooKeeperSessionState.AuthFailed);
        var factory = Substitute.For<IZkLinkConnectionFactory>();
        var options = CreateOptions();
        factory
            .ConnectAsync(options, Arg.Any<CancellationToken>())
            .Returns(
                _ => Task.FromResult(first),
                _ => Task.FromException<IZkLinkConnection>(new IOException("Replacement failed."))
            );
        var stopping = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var application = Substitute.For<IHostApplicationLifetime>();
        application.When(a => a.StopApplication()).Do(_ => stopping.TrySetResult());
        int captures = 0;
        var host = new ZkLinkConnectionHost(
            () =>
                ++captures == 2 && factoryThrows
                    ? throw new InvalidOperationException("Options unavailable.")
                    : options,
            factory,
            ZkLinkTerminalPolicy.Recreate,
            application
        );
        await host.StartAsync(token);
        ended.SetResult();
        await stopping.Task.WaitAsync(token);
        Assert.Equal(2, captures);
        Assert.Same(first.Client, host.Client);
        application.Received(1).StopApplication();
        await host.StopAsync(token);
        await host.DisposeAsync();
        await first.Received(1).CloseAsync(token);
        await first.Received(1).DisposeAsync();
    }

    [Fact(Timeout = 10000)]
    public async Task HostStoppingCallbackCanShutDownTheSessionBeforeReturning()
    {
        var token = TestContext.Current.CancellationToken;
        var first = Substitute.For<IZkLinkConnection>();
        var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        first.Completion.Returns(ended.Task);
        first.State.Returns(ZooKeeperSessionState.Lost);
        var factory = Substitute.For<IZkLinkConnectionFactory>();
        var options = CreateOptions();
        factory.ConnectAsync(options, Arg.Any<CancellationToken>()).Returns(first);
        var application = Substitute.For<IHostApplicationLifetime>();
        var host = new ZkLinkConnectionHost(
            () => options,
            factory,
            ZkLinkTerminalPolicy.StopApplication,
            application
        );
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var returned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        application
            .When(a => a.StopApplication())
            .Do(_ =>
            {
                entered.TrySetResult();
                release.Wait(token);
                returned.TrySetResult();
            });
        await host.StartAsync(token);
        ended.SetResult();
        try
        {
            await entered.Task.WaitAsync(token);
            // Generic Host can start its shutdown before all stopping callbacks finish.
            await host.StopAsync(token).WaitAsync(token);
            await first.Received(1).CloseAsync(token);
            Assert.False(returned.Task.IsCompleted);
        }
        finally
        {
            release.Set();
            await returned.Task.WaitAsync(token);
            await host.DisposeAsync();
        }
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
        var host = new ZkLinkConnectionHost(() => options, factory);
        startTask = host.StartAsync(TestContext.Current.CancellationToken);
        return (host, connection, factory);
    }

    private static ZooKeeperClientOptions CreateOptions() =>
        new() { ConnectionString = "localhost:2181" };
}
