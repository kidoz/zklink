using ZkLink;

namespace ZkLink.Analyzers.PackageSmoke;

public static class AnalyzerPackageSmoke
{
    public static async Task Run(ZooKeeperClient client, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(client);
#if EXPECT_ANALYZER_DIAGNOSTIC
        client.SubscribeStateChanges();
        client.SyncAsync("/node", cancellationToken);
#else
        var states = client.SubscribeStateChanges();
        await using var lifetime = states.ConfigureAwait(false);
        await client.SyncAsync("/node", cancellationToken).ConfigureAwait(false);
#endif
    }
}
