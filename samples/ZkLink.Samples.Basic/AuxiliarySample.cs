using ZkLink;

namespace ZkLink.Samples.Basic;

internal sealed class AuxiliarySample : ISampleStep
{
    public async Task<string> RunAsync(ZooKeeperClient client, CancellationToken cancellationToken)
    {
        var path = SampleNodes.UniquePath("/zklink-auxiliary-sample-");
        await client
            .CreateAsync(path, null, SampleNodes.EphemeralCreatorAcl(), cancellationToken)
            .ConfigureAwait(false);
        var ephemerals = await client
            .GetEphemeralsAsync(path, cancellationToken)
            .ConfigureAwait(false);
        var descendants = await client
            .GetAllChildrenNumberAsync(path, cancellationToken)
            .ConfigureAwait(false);
        var identities = await client.WhoAmIAsync(cancellationToken).ConfigureAwait(false);
        if (
            !ephemerals.Contains(path, StringComparer.Ordinal)
            || descendants != 0
            || !identities.Any(static identity => identity.Scheme == "digest")
        )
        {
            throw new InvalidOperationException(
                "Auxiliary operation results did not match the sample's setup."
            );
        }

        await client.DeleteAsync(path, ZNodeVersion.Any, cancellationToken).ConfigureAwait(false);
        return "Ephemeral listing, descendant count and identity query completed.";
    }
}
