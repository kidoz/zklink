using ZkLink;

namespace ZkLink.Samples.Basic;

internal sealed class TransactionSample : ISampleStep
{
    public async Task<string> RunAsync(ZooKeeperClient client, CancellationToken cancellationToken)
    {
        var path = SampleNodes.UniquePath("/zklink-transaction-sample-");
        var creatorAcl = SampleNodes.EphemeralCreatorAcl();
        await client
            .MultiAsync(
                [
                    ZooKeeperWriteOperation.Create(path, "initial"u8.ToArray(), creatorAcl),
                    ZooKeeperWriteOperation.SetData(
                        path,
                        "updated"u8.ToArray(),
                        ZNodeVersion.Exact(0)
                    ),
                    ZooKeeperWriteOperation.Check(path, ZNodeVersion.Exact(1)),
                ],
                cancellationToken
            )
            .ConfigureAwait(false);
        await client
            .MultiReadAsync([ZooKeeperReadOperation.GetData(path)], cancellationToken)
            .ConfigureAwait(false);
        await client
            .MultiAsync(
                [ZooKeeperWriteOperation.Delete(path, ZNodeVersion.Exact(1))],
                cancellationToken
            )
            .ConfigureAwait(false);
        return "Transaction create/update/check/read/delete completed.";
    }
}
