namespace ZkLink.Extensions.DependencyInjection;

internal interface IZkLinkConnectionFactory
{
    Task<IZkLinkConnection> ConnectAsync(
        ZooKeeperClientOptions options,
        CancellationToken cancellationToken
    );
}

internal sealed class ZkLinkConnectionFactory : IZkLinkConnectionFactory
{
    public async Task<IZkLinkConnection> ConnectAsync(
        ZooKeeperClientOptions options,
        CancellationToken cancellationToken
    )
    {
        var client = await ZooKeeperClient
            .ConnectAsync(options, cancellationToken)
            .ConfigureAwait(false);
        return new ZkLinkConnection(client);
    }
}
