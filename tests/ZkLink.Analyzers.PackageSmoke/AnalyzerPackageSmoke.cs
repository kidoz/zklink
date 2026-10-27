using ZkLink;

namespace ZkLink.Analyzers.PackageSmoke;

public static class AnalyzerPackageSmoke
{
    public static async Task Run(ZooKeeperClient client, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(client);
#if EXPECT_ANALYZER_DIAGNOSTIC
        client.SyncAsync("/node", cancellationToken);
#else
        await client.SyncAsync("/node", cancellationToken).ConfigureAwait(false);
#endif
    }
}
