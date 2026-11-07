# ZkLink.Extensions.DependencyInjection

Registers one host-owned ZooKeeper session with Microsoft.Extensions.DependencyInjection. The core `ZkLink` project has no Microsoft.Extensions dependency.

```csharp
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ZkLink;
using ZkLink.Extensions.DependencyInjection;

// Register ZkLink before hosted services that use it with sequential host startup.
services.AddZkLink(new ZooKeeperClientOptions
{
    ConnectionString = "zk.example:2281/app"
});
services.AddHostedService<CoordinationWorker>();

sealed class CoordinationWorker(IZooKeeperClientAccessor clients) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        ZooKeeperClient client = clients.Client;
        await client.SyncAsync("/", stoppingToken);
    }
}
```

Inject the singleton `IZooKeeperClientAccessor` into constructors. Its `Client` property returns the same shared client after successful ZkLink startup and before shutdown; accessing that property earlier or later throws. The accessor can be resolved safely before startup and from synchronous or asynchronous child scopes. Resolving it does not acquire client disposal ownership.

`ZooKeeperClient.ConnectAsync` runs during host startup. Keep the default sequential startup and register ZkLink before dependent hosted services. Concurrent startup requires an application-defined readiness barrier before reading `Client`. The accessor does not connect on demand or replace a lost session.

The host owns shutdown: graceful shutdown uses acknowledged close, and asynchronous container disposal releases resources. The returned client is a borrowed view; its `DisposeAsync` does not dispose the host session. Explicitly calling `CloseAsync` closes the shared session for every consumer.

The unpublished direct `ZooKeeperClient` registration has been replaced: migrate constructor parameters and service resolution to `IZooKeeperClientAccessor`, and read `Client` when performing work rather than in the constructor.

Calling `AddZkLink` twice in one service collection throws. Options, credentials and certificates are captured when the host connects, so keep configuration resources alive until startup completes. Use an explicitly owned `ZooKeeperClient` for a separate security or coordination identity. The helper does not add session pooling, request replay, named registrations, health checks, or configuration binding.

The project produces local NuGet and portable-symbol packages. Publication, cross-platform installed-package qualification and Native AOT qualification remain separate release checks; no DI-specific Native AOT support is claimed.
