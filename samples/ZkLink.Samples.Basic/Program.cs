using ZkLink;

if (
    args.Length is < 1 or > 6
    || args.Skip(1)
        .Any(value =>
            value
                is not (
                    "--plaintext"
                    or "--nodes"
                    or "--transactions"
                    or "--auxiliary"
                    or "--watches"
                )
        )
)
{
    await Console
        .Error.WriteLineAsync(
            "Usage: ZkLink.Samples.Basic host:port[,host:port] [--plaintext] [--nodes] [--transactions] [--auxiliary] [--watches]"
        )
        .ConfigureAwait(false);
    return 2;
}

using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
var user = Environment.GetEnvironmentVariable("ZKLINK_USER");
var password = Environment.GetEnvironmentVariable("ZKLINK_PASSWORD");
var nodes = args.Contains("--nodes", StringComparer.Ordinal);
var transactions = args.Contains("--transactions", StringComparer.Ordinal);
var auxiliary = args.Contains("--auxiliary", StringComparer.Ordinal);
var watches = args.Contains("--watches", StringComparer.Ordinal);
if (
    (user is null) != (password is null)
    || ((nodes || transactions || auxiliary || watches) && user is null)
)
{
    await Console
        .Error.WriteLineAsync(
            "Set both ZKLINK_USER and ZKLINK_PASSWORD; --nodes, --transactions, --auxiliary and --watches require an authenticated creator ACL."
        )
        .ConfigureAwait(false);
    return 2;
}
using var authentication =
    user is null || password is null ? null : ZooKeeperAuthentication.Digest(user, password);
var plaintext = args.Contains("--plaintext", StringComparer.Ordinal);
var options = new ZooKeeperClientOptions
{
    ConnectionString = args[0],
    Tls = plaintext ? ZooKeeperTlsOptions.Disabled() : ZooKeeperTlsOptions.Required(),
    Authentication = authentication is null ? [] : [authentication],
    // This switch is for the explicitly selected local plaintext fixture only.
    AllowUnsafePlaintextDigest = plaintext,
};

try
{
    var client = await ZooKeeperClient.ConnectAsync(options, deadline.Token).ConfigureAwait(false);
    await using var clientLifetime = client.ConfigureAwait(false);
    if (nodes)
    {
        var path = "/zklink-sample-" + Guid.NewGuid().ToString("N");
        var createOptions = new CreateOptions(
            [new(ZooKeeperPermissions.All, "auth", "")],
            CreateMode.Ephemeral
        );
        await client
            .CreateAsync(path, "initial"u8.ToArray(), createOptions, deadline.Token)
            .ConfigureAwait(false);
        var current = await client.GetDataAsync(path, deadline.Token).ConfigureAwait(false);
        var updated = await client
            .SetDataAsync(
                path,
                "updated"u8.ToArray(),
                ZNodeVersion.Exact(current.Stat.Version),
                deadline.Token
            )
            .ConfigureAwait(false);
        await client
            .DeleteAsync(path, ZNodeVersion.Exact(updated.Version), deadline.Token)
            .ConfigureAwait(false);
        await Console
            .Out.WriteLineAsync("Node create/read/update/delete completed.")
            .ConfigureAwait(false);
    }
    if (transactions)
    {
        var path = "/zklink-transaction-sample-" + Guid.NewGuid().ToString("N");
        var creatorAcl = new CreateOptions(
            [new(ZooKeeperPermissions.All, "auth", "")],
            CreateMode.Ephemeral
        );
        await client
            .MultiAsync(
                [
                    ZooKeeperWriteOperation.Create(path, "initial"u8.ToArray(), creatorAcl),
                    ZooKeeperWriteOperation.SetData(
                        path,
                        "updated"u8.ToArray(),
                        ZNodeVersion.Exact(0)
                    ),
                    ZooKeeperWriteOperation.Check(path, ZNodeVersion.Exact(1)),
                ],
                deadline.Token
            )
            .ConfigureAwait(false);
        await client
            .MultiReadAsync([ZooKeeperReadOperation.GetData(path)], deadline.Token)
            .ConfigureAwait(false);
        await client
            .MultiAsync(
                [ZooKeeperWriteOperation.Delete(path, ZNodeVersion.Exact(1))],
                deadline.Token
            )
            .ConfigureAwait(false);
        await Console
            .Out.WriteLineAsync("Transaction create/update/check/read/delete completed.")
            .ConfigureAwait(false);
    }
    if (auxiliary)
    {
        var path = "/zklink-auxiliary-sample-" + Guid.NewGuid().ToString("N");
        var creatorAcl = new CreateOptions(
            [new(ZooKeeperPermissions.All, "auth", "")],
            CreateMode.Ephemeral
        );
        await client.CreateAsync(path, null, creatorAcl, deadline.Token).ConfigureAwait(false);
        var ephemerals = await client
            .GetEphemeralsAsync(path, deadline.Token)
            .ConfigureAwait(false);
        var descendants = await client
            .GetAllChildrenNumberAsync(path, deadline.Token)
            .ConfigureAwait(false);
        var identities = await client.WhoAmIAsync(deadline.Token).ConfigureAwait(false);
        if (
            !ephemerals.Contains(path, StringComparer.Ordinal)
            || descendants != 0
            || !identities.Any(identity => identity.Scheme == "digest")
        )
            throw new InvalidOperationException(
                "Auxiliary operation results did not match the sample's setup."
            );
        await client.DeleteAsync(path, ZNodeVersion.Any, deadline.Token).ConfigureAwait(false);
        await Console
            .Out.WriteLineAsync("Ephemeral listing, descendant count and identity query completed.")
            .ConfigureAwait(false);
    }
    if (watches)
    {
        var path = "/zklink-watch-sample-" + Guid.NewGuid().ToString("N");
        var watched = await client.ExistsAndWatchAsync(path, deadline.Token).ConfigureAwait(false);
        await using var watchLifetime = watched.Watch.ConfigureAwait(false);
        if (watched.Value is not null)
            throw new InvalidOperationException("The sample path already exists.");
        await client
            .CreateAsync(
                path,
                null,
                new CreateOptions(
                    [new(ZooKeeperPermissions.All, "auth", "")],
                    CreateMode.Ephemeral
                ),
                deadline.Token
            )
            .ConfigureAwait(false);
        var events = 0;
        await foreach (
            var change in watched.Watch.ReadEventsAsync(deadline.Token).ConfigureAwait(false)
        )
        {
            if (change.Kind != ZooKeeperWatchEventKind.NodeCreated)
                throw new InvalidOperationException("The watch returned an unexpected event.");
            events++;
        }
        if (events != 1)
            throw new InvalidOperationException("The one-shot watch did not deliver exactly once.");
        await client.DeleteAsync(path, ZNodeVersion.Any, deadline.Token).ConfigureAwait(false);
        await Console
            .Out.WriteLineAsync(
                "Missing-node watch, buffered creation event and stream completion verified."
            )
            .ConfigureAwait(false);
    }
    await client.SyncAsync("/", deadline.Token).ConfigureAwait(false);
    Console.WriteLine(
        FormattableString.Invariant(
            $"State: {client.Session.State}; generation: {client.Session.ConnectionGeneration}; negotiated timeout: {client.Session.NegotiatedTimeout}"
        )
    );
    await client.CloseAsync(deadline.Token).ConfigureAwait(false);
    Console.WriteLine($"Close: {client.Session.CloseStatus}");
    return 0;
}
catch (Exception error)
    when (error
            is ZooKeeperConnectionException
                or ZooKeeperSessionException
                or ZooKeeperException
                or OperationCanceledException
    )
{
    await Console.Error.WriteLineAsync(error.Message).ConfigureAwait(false);
    return 1;
}
