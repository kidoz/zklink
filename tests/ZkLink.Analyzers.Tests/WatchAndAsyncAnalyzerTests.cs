namespace ZkLink.Analyzers.Tests;

public sealed class WatchAndAsyncAnalyzerTests
{
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
