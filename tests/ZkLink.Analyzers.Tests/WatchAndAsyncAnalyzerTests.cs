namespace ZkLink.Analyzers.Tests;

public sealed class WatchAndAsyncAnalyzerTests
{
    [Fact]
    [Trait("Acceptance", "T41")]
    public async Task DiscardedStateSubscriptionsWarnOnlyForUnownedClientResults()
    {
        var diagnostics = await AnalyzerTestHarness.AnalyzeAsync(
            """
            using System.Threading.Tasks;
            using ZkLink;
            public static class Example
            {
                public static async Task Run(ZooKeeperClient client)
                {
                    client.SubscribeStateChanges();
                    _ = client.SubscribeStateChanges();
                    await using var owned = client.SubscribeStateChanges();
                    SubscribeStateChanges();
                }
                public static int SubscribeStateChanges() => 0;
                public static ZooKeeperStateSubscription Retain(ZooKeeperClient client)
                {
                    ZooKeeperStateSubscription _;
                    _ = client.SubscribeStateChanges();
                    return _;
                }
            }
            """,
            new DiscardedWatchAnalyzer()
        );
        Assert.Equal(2, diagnostics.Length);
        Assert.All(diagnostics, diagnostic => Assert.Equal("ZKL0007", diagnostic.Id));
    }

    [Fact]
    [Trait("Acceptance", "T41")]
    public async Task DiscardedWatchCoversDirectAwaitedAndDiscardAssignmentPatterns()
    {
        var diagnostics = await AnalyzerTestHarness.AnalyzeAsync(
            """
            using System.Threading.Tasks;
            using ZkLink;
            public static class Example
            {
                public static async Task Run(ZooKeeperClient client)
                {
                    client.AddWatchAsync("/node", PersistentWatchMode.Persistent);
                    await client.ExistsAndWatchAsync("/node");
                    _ = await client.GetDataAndWatchAsync("/node");
                    _ = client.GetChildrenAndWatchAsync("/node");
                    var owned = await client.AddWatchAsync("/node", PersistentWatchMode.Persistent);
                    await owned.DisposeAsync();
                }
            }
            """,
            new DiscardedWatchAnalyzer()
        );

        Assert.Equal(4, diagnostics.Length);
        Assert.All(diagnostics, diagnostic => Assert.Equal("ZKL0003", diagnostic.Id));
    }

    [Fact]
    [Trait("Acceptance", "T41")]
    public async Task UnobservedCallsIncludeTasksValueTasksAndDiscards()
    {
        var diagnostics = await AnalyzerTestHarness.AnalyzeAsync(
            """
            using System.Threading.Tasks;
            using ZkLink;
            public static class Example
            {
                public static async Task Run(
                    ZooKeeperClient client,
                    ZooKeeperWatch watch,
                    ZooKeeperClientOptions options)
                {
                    ZooKeeperClient.ConnectAsync(options);
                    client.SyncAsync("/node");
                    _ = client.GetDataAsync("/node");
                    watch.RemoveAsync();
                    client.DisposeAsync();
                    await client.SyncAsync("/node");
                    var pending = client.SyncAsync("/node");
                    await pending;
                }

                public static async Task Observe(ZooKeeperClient client)
                {
                    Task _;
                    _ = client.SyncAsync("/node");
                    await _;
                }
            }
            """,
            new UnobservedAsyncCallAnalyzer()
        );

        Assert.Equal(5, diagnostics.Length);
        Assert.All(diagnostics, diagnostic => Assert.Equal("ZKL0004", diagnostic.Id));
    }

    [Fact]
    [Trait("Acceptance", "T41")]
    public async Task WatchDiscardProducesOnlyWatchDiagnostic()
    {
        var diagnostics = await AnalyzerTestHarness.AnalyzeAsync(
            """
            using ZkLink;
            public static class Example
            {
                public static void Run(ZooKeeperClient client)
                {
                    client.AddWatchAsync("/node", PersistentWatchMode.Persistent);
                }
            }
            """,
            new DiscardedWatchAnalyzer(),
            new UnobservedAsyncCallAnalyzer()
        );

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("ZKL0003", diagnostic.Id);
    }

    [Fact]
    [Trait("Acceptance", "T41")]
    public async Task MissingCancellationRequiresAvailableTokenAndOmitsExplicitChoice()
    {
        var diagnostics = await AnalyzerTestHarness.AnalyzeAsync(
            """
            using System.Threading;
            using System.Threading.Tasks;
            using ZkLink;
            public static class Example
            {
                public static async Task Run(
                    ZooKeeperClient client,
                    ZooKeeperWatch watch,
                    ZooKeeperClientOptions options,
                    CancellationToken cancellationToken)
                {
                    await client.SyncAsync("/node");
                    await watch.RemoveAsync();
                    await ZooKeeperClient.ConnectAsync(options);
                    await client.SyncAsync("/node", default);
                    await client.SyncAsync("/node", cancellationToken);
                }

                public static void NoToken(ZooKeeperClient client) => client.SyncAsync("/node");
            }
            """,
            new MissingCancellationTokenAnalyzer()
        );

        Assert.Equal(3, diagnostics.Length);
        Assert.All(diagnostics, diagnostic => Assert.Equal("ZKL0005", diagnostic.Id));
    }

    [Fact]
    [Trait("Acceptance", "T41")]
    public async Task MissingCancellationFindsTokenFromOuterScope()
    {
        var diagnostics = await AnalyzerTestHarness.AnalyzeAsync(
            """
            using System;
            using System.Threading;
            using System.Threading.Tasks;
            using ZkLink;
            public static class Example
            {
                public static void Run(ZooKeeperClient client, CancellationToken cancellationToken)
                {
                    async Task Local() => await client.SyncAsync("/node");
                    Func<Task> lambda = async () => await client.SyncAsync("/node");
                }
            }
            """,
            new MissingCancellationTokenAnalyzer()
        );

        Assert.Equal(2, diagnostics.Length);
        Assert.All(diagnostics, diagnostic => Assert.Equal("ZKL0005", diagnostic.Id));
    }

    [Fact]
    [Trait("Acceptance", "T41")]
    public async Task MissingCancellationRespectsLambdaParametersAndStaticCaptureBoundaries()
    {
        var diagnostics = await AnalyzerTestHarness.AnalyzeAsync(
            """
            using System;
            using System.Threading;
            using System.Threading.Tasks;
            using ZkLink;
            public static class Example
            {
                public static Func<CancellationToken, Task> Callback(ZooKeeperClient client) =>
                    token => client.SyncAsync("/available");

                public static void Run(CancellationToken outer)
                {
                    static Task Local(ZooKeeperClient c) => c.SyncAsync("/local");
                    Func<ZooKeeperClient, Task> simple = static c => c.SyncAsync("/simple");
                    Func<ZooKeeperClient, Task> parenthesized = static (c) => c.SyncAsync("/paren");
                    Func<ZooKeeperClient, CancellationToken, Task> own =
                        static (c, token) => c.SyncAsync("/own");
                }
            }
            """,
            new MissingCancellationTokenAnalyzer()
        );

        var calls = diagnostics.Select(diagnostic =>
            diagnostic.Location.SourceTree!.GetText().ToString(diagnostic.Location.SourceSpan)
        );
        Assert.Equal(
            ["""c.SyncAsync("/own")""", """client.SyncAsync("/available")"""],
            calls.Order(StringComparer.Ordinal)
        );
    }

    [Fact]
    [Trait("Acceptance", "T41")]
    public async Task SyncOverAsyncCoversResultWaitAndGetResultOnlyOnDirectCalls()
    {
        var diagnostics = await AnalyzerTestHarness.AnalyzeAsync(
            """
            using System.Threading.Tasks;
            using ZkLink;
            public static class Example
            {
                public static async Task Run(ZooKeeperClient client, ZooKeeperClientOptions options)
                {
                    var connected = ZooKeeperClient.ConnectAsync(options).Result;
                    client.SyncAsync("/node").Wait();
                    client.SyncAsync("/node").GetAwaiter().GetResult();
                    var data = client.GetDataAsync("/node").GetAwaiter().GetResult();
                    var pending = client.GetDataAsync("/node");
                    pending.Wait();
                    await client.SyncAsync("/node");
                }
            }
            """,
            new SyncOverAsyncAnalyzer()
        );

        Assert.Equal(4, diagnostics.Length);
        Assert.All(diagnostics, diagnostic => Assert.Equal("ZKL0006", diagnostic.Id));
    }
}
