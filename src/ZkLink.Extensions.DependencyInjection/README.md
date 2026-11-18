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

Inject `IZooKeeperClientAccessor` into constructors. Read its `Client` after
successful startup to obtain the current shared client. Disposing a child scope
or the borrowed client view preserves the host session. Explicitly calling
`CloseAsync` on that view closes the session for every consumer.

`AddZkLink` accepts an options object or a factory that receives the root
`IServiceProvider`. The host invokes the factory during startup. An application
that resolves `ZooKeeperClientOptions` as a service must register its own
instance. Keep application-owned authentication objects and certificates alive
until startup has captured them. A factory used for session replacement must
supply valid credentials on each invocation.

Startup waits for connection readiness within `InitialConnectTimeout` and
propagates connection failures. Register ZkLink before dependent services for
sequential startup. Concurrent startup requires an application readiness barrier.
During shutdown the accessor becomes unavailable, the host waits for any pending
replacement to finish cleanup, and graceful shutdown requests acknowledged close.

The factory overload accepts a `ZkLinkTerminalPolicy`. The default `Fail`
keeps the terminal client available for inspection. `StopApplication` requests
graceful host shutdown. `Recreate` invokes the options factory again to start
a new ZooKeeper session. Read the accessor again to obtain the replacement and
rebuild watches, ephemerals and coordination state as appropriate. A failed
replacement requests host shutdown. Ordinary disconnections use the core
client’s same-session recovery.

Hosts using Microsoft health checks can add
`services.AddHealthChecks().AddCheck<ZkLinkHealthCheck>("zookeeper")`.
The check reads the current session state locally. Connected is healthy,
connecting or recovering is degraded, and a terminal or unavailable session
is unhealthy.

Calling `AddZkLink` twice in one service collection throws. Use an explicitly
owned client for a separate security or coordination identity. The factory,
terminal policies and health check are available in the development source
following the 1.0.3 release.

Native AOT verification uses packed core and DI packages, a rooted analysis
consumer and an unrooted native host. The host checks constructor injection,
shared access across synchronous and asynchronous scopes, borrowed-client disposal,
live operations, acknowledged shutdown and ephemeral cleanup. The native workflow
runs these checks on Linux x64/arm64, Windows x64 and macOS arm64, against
ZooKeeper 3.8.7 and 3.9.6 with TLS and mTLS. See the
[native verification guide](../../docs/how-to/verify-native-aot.md) for commands
and evidence scope. This verifies the documented host integration; it does not
establish compatibility for arbitrary application registrations or dependencies.

The public **1.0.2** packages passed this matrix in
[verification run 42410311495](https://github.com/kidoz/zklink/actions/runs/42410311495):
all eight jobs passed, with 48 native DI lifecycle cases and no trim/AOT warnings.
