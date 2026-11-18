# Register ZkLink with dependency injection

Use `ZkLink.Extensions.DependencyInjection` when a Microsoft host should own one shared ZooKeeper session.

Install `ZkLink.Extensions.DependencyInjection`, then register the client:

```csharp
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ZkLink;
using ZkLink.Extensions.DependencyInjection;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddZkLink(new ZooKeeperClientOptions
{
    ConnectionString = "zk.example:2281/app"
});

using var host = builder.Build();
var accessor = host.Services.GetRequiredService<IZooKeeperClientAccessor>();
await host.StartAsync();

var zooKeeper = accessor.Client;
var stat = await zooKeeper.ExistsAsync("/config");

await host.StopAsync();
```

TLS remains required by default. For the repository's plaintext local fixture only, set `Tls = ZooKeeperTlsOptions.Disabled()` in the options object.

The hosted registration connects asynchronously during `StartAsync`; it never blocks a service factory. Constructor-inject `IZooKeeperClientAccessor` and read its `Client` when doing work. The accessor is shared across scopes and can be resolved before startup; reading `Client` before startup or after shutdown throws an `InvalidOperationException`.

Register ZkLink before dependent hosted services and keep sequential startup. A dependent service can constructor-inject the accessor and read `Client` from its `StartAsync` method. Concurrent startup requires an application-defined readiness barrier.

`StopAsync` uses the client's acknowledged close. Disposing the container without graceful shutdown still releases local resources. A second `AddZkLink` call in the same service collection throws rather than silently selecting another connection or identity. Create and own a separate client explicitly when independent identities are required.

### Startup, health and terminal sessions

Pass a factory to resolve application services when the host starts, for example
`services.AddZkLink(sp => sp.GetRequiredService<MySettings>().CreateClientOptions())`.
The options belong to this registration. Applications that inject
`ZooKeeperClientOptions` directly must register their own instance. Keep input
credentials and certificates valid until the host captures them. A replacement
factory must supply valid credentials each time it runs. Startup waits for
readiness within `InitialConnectTimeout` and propagates connection failures.

Applications using Microsoft health checks can register
`services.AddHealthChecks().AddCheck<ZkLinkHealthCheck>("zookeeper")`.
The check reads local session state. Connected is healthy, connecting or
recovering is degraded, and a terminal or unavailable client is unhealthy.

The factory overload accepts a terminal policy. The default `Fail` keeps the
terminal client available for inspection. `StopApplication` requests graceful
host shutdown. `Recreate` calls the factory again to establish a new session.
Read the accessor again to get its new client. Watches, ephemerals and locks
belong to the previous session. A failed replacement requests host shutdown.

See the [connection reference](../reference/connections.md) for lifecycle and close details.
The [Native AOT verification guide](verify-native-aot.md) describes packed-package
host lifecycle checks and how to verify a published package version.

[How-to guides](README.md) · [Documentation home](../README.md)
