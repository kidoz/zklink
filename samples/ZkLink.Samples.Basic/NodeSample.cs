using ZkLink;

namespace ZkLink.Samples.Basic;

internal sealed class NodeSample : ISampleStep
{
    public async Task<string> RunAsync(ZooKeeperClient client, CancellationToken cancellationToken)
    {
        var path = SampleNodes.UniquePath("/zklink-sample-");
        var createOptions = SampleNodes.EphemeralCreatorAcl();
        await client
            .CreateAsync(path, "initial"u8.ToArray(), createOptions, cancellationToken)
            .ConfigureAwait(false);
        var current = await client.GetDataAsync(path, cancellationToken).ConfigureAwait(false);
        var updated = await client
            .SetDataAsync(
                path,
                "updated"u8.ToArray(),
                ZNodeVersion.Exact(current.Stat.Version),
                cancellationToken
            )
            .ConfigureAwait(false);
        await client
            .DeleteAsync(path, ZNodeVersion.Exact(updated.Version), cancellationToken)
            .ConfigureAwait(false);
        return "Node create/read/update/delete completed.";
    }
}
