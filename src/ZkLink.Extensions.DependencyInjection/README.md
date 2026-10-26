# ZkLink.Extensions.DependencyInjection

Registers one host-owned ZooKeeper session with Microsoft.Extensions.DependencyInjection. The core `ZkLink` project has no Microsoft.Extensions dependency.

```csharp
using Microsoft.Extensions.DependencyInjection;
using ZkLink;

services.AddZkLink(new ZooKeeperClientOptions
{
    ConnectionString = "zk.example:2281/app"
});
```

`ZooKeeperClient.ConnectAsync` runs during host startup. Resolve the shared `ZooKeeperClient` after startup and before shutdown; earlier or later resolution throws. Graceful shutdown uses acknowledged close, and asynchronous container disposal releases local resources.

Calling `AddZkLink` twice in one service collection throws. Use an explicitly owned `ZooKeeperClient` for a separate security or coordination identity. The helper does not add session pooling, request replay, named registrations, health checks, or configuration binding.

The source project and server-free lifecycle tests are available in this repository. No NuGet package, installed-package consumer, or Native AOT qualification is currently claimed.
