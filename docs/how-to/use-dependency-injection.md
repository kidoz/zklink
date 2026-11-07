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

See the [connection reference](../reference/connections.md) for lifecycle and close details.
The [Native AOT verification guide](verify-native-aot.md) describes packed-package
host lifecycle checks and how to verify a published package version.

[How-to guides](README.md) · [Documentation home](../README.md)
