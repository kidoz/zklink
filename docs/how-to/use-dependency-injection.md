# Register ZkLink with dependency injection

Use `ZkLink.Extensions.DependencyInjection` when a Microsoft host should own one shared ZooKeeper session.

Add a project reference from the application to `src/ZkLink.Extensions.DependencyInjection`, then register the client:

```csharp
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ZkLink;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddZkLink(new ZooKeeperClientOptions
{
    ConnectionString = "zk.example:2281/app"
});

using var host = builder.Build();
await host.StartAsync();

var zooKeeper = host.Services.GetRequiredService<ZooKeeperClient>();
var stat = await zooKeeper.ExistsAsync("/config");

await host.StopAsync();
```

TLS remains required by default. For the repository's plaintext local fixture only, set `Tls = ZooKeeperTlsOptions.Disabled()` in the options object.

The hosted registration connects asynchronously during `StartAsync`; it never blocks a service factory. The same `ZooKeeperClient` is injected into ordinary application services created while the host is started. Resolving it before startup or after shutdown throws an `InvalidOperationException`.

Do not inject the client into another hosted service's constructor: .NET constructs hosted services before running their startup methods. Resolve application dependencies after startup instead, or keep explicit client ownership when a different startup sequence is required.

`StopAsync` uses the client's acknowledged close. Disposing the container without graceful shutdown still releases local resources. A second `AddZkLink` call in the same service collection throws rather than silently selecting another connection or identity. Create and own a separate client explicitly when independent identities are required.

See the [connection reference](../reference/connections.md) for lifecycle and close details.

[How-to guides](README.md) · [Documentation home](../README.md)
