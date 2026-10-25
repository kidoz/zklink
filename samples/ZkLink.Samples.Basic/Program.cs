using ZkLink;

if (
    args.Length is < 1 or > 3
    || args.Skip(1).Any(value => value is not ("--plaintext" or "--nodes"))
)
{
    await Console
        .Error.WriteLineAsync(
            "Usage: ZkLink.Samples.Basic host:port[,host:port] [--plaintext] [--nodes]"
        )
        .ConfigureAwait(false);
    return 2;
}

using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
var user = Environment.GetEnvironmentVariable("ZKLINK_USER");
var password = Environment.GetEnvironmentVariable("ZKLINK_PASSWORD");
var nodes = args.Contains("--nodes", StringComparer.Ordinal);
if ((user is null) != (password is null) || (nodes && user is null))
{
    await Console
        .Error.WriteLineAsync(
            "Set both ZKLINK_USER and ZKLINK_PASSWORD; --nodes requires an authenticated creator ACL."
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
