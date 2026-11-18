namespace ZkLink.Extensions.DependencyInjection;

internal interface IZkLinkConnection : IAsyncDisposable
{
    ZooKeeperClient Client { get; }

    Task Completion { get; }
    ZooKeeperSessionState State { get; }

    Task CloseAsync(CancellationToken cancellationToken);
}

internal sealed class ZkLinkConnection : IZkLinkConnection
{
    private readonly ZooKeeperClient _ownedClient;

    public ZkLinkConnection(ZooKeeperClient client)
    {
        _ownedClient = client;
        Client = client.CreateNonOwningView();
    }

    public ZooKeeperClient Client { get; }

    public Task Completion => _ownedClient.Manager.Completion;
    public ZooKeeperSessionState State => _ownedClient.Session.State;

    public Task CloseAsync(CancellationToken cancellationToken) =>
        _ownedClient.CloseAsync(cancellationToken);

    public ValueTask DisposeAsync() => _ownedClient.DisposeAsync();
}
