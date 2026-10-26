namespace ZkLink.Extensions.DependencyInjection;

internal interface IZkLinkConnection : IAsyncDisposable
{
    ZooKeeperClient Client { get; }

    Task CloseAsync(CancellationToken cancellationToken);
}

internal sealed class ZkLinkConnection(ZooKeeperClient client) : IZkLinkConnection
{
    public ZooKeeperClient Client { get; } = client;

    public Task CloseAsync(CancellationToken cancellationToken) =>
        Client.CloseAsync(cancellationToken);

    public ValueTask DisposeAsync() => Client.DisposeAsync();
}
