using System.Security.Cryptography;
using Microsoft.Extensions.DependencyInjection;
using ZkLink;
using ZkLink.Extensions.DependencyInjection;

CheckAssembly(typeof(ZooKeeperClient), "ZKLINK_EXPECTED_CORE_SHA256");
CheckAssembly(typeof(IZooKeeperClientAccessor), "ZKLINK_EXPECTED_DI_SHA256");
var services = new ServiceCollection();
services.AddZkLink(new ZooKeeperClientOptions { ConnectionString = "127.0.0.1:2181" });
await using var provider = services.BuildServiceProvider();
var accessor = provider.GetRequiredService<IZooKeeperClientAccessor>();
if (accessor is IDisposable or IAsyncDisposable)
{
    throw new InvalidOperationException(
        "The accessor must not acquire session disposal ownership."
    );
}
for (int index = 0; index < 10_000; index++)
{
    if (!ReferenceEquals(accessor, provider.GetRequiredService<IZooKeeperClientAccessor>()))
    {
        throw new InvalidOperationException("The accessor must be shared.");
    }
}
using (var scope = provider.CreateScope())
{
    if (
        !ReferenceEquals(
            accessor,
            scope.ServiceProvider.GetRequiredService<IZooKeeperClientAccessor>()
        )
    )
    {
        throw new InvalidOperationException("A child scope must use the shared accessor.");
    }
}
bool rejectedBeforeStartup = false;
try
{
    _ = accessor.Client;
}
catch (InvalidOperationException)
{
    rejectedBeforeStartup = true;
}
if (!rejectedBeforeStartup)
{
    throw new InvalidOperationException("An unstarted host must not expose a client.");
}
Console.WriteLine("Release package identities and DI registration: PASS");

static void CheckAssembly(Type type, string variable)
{
    string expected =
        Environment.GetEnvironmentVariable(variable)
        ?? throw new InvalidOperationException("Missing expected package assembly hash.");
    string actual = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(type.Assembly.Location)));
    if (actual != expected)
    {
        throw new InvalidOperationException("Loaded assembly differs from the verified package.");
    }
}

public static class AnalyzerCompilationCheck
{
    public static async Task ObserveAsync(
        ZooKeeperClient client,
        CancellationToken cancellationToken
    )
    {
#if EXPECT_ANALYZER_DIAGNOSTIC
        client.SyncAsync("/", cancellationToken);
#else
        await client.SyncAsync("/", cancellationToken).ConfigureAwait(false);
#endif
    }
}
