using ZkLink;

if (args.Length is < 1 or > 2 || (args.Length == 2 && args[1] != "--plaintext"))
{
    await Console
        .Error.WriteLineAsync("Usage: ZkLink.Samples.Basic host:port[,host:port] [--plaintext]")
        .ConfigureAwait(false);
    return 2;
}

using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
var user = Environment.GetEnvironmentVariable("ZKLINK_USER");
var password = Environment.GetEnvironmentVariable("ZKLINK_PASSWORD");
using var authentication =
    user is null || password is null ? null : ZooKeeperAuthentication.Digest(user, password);
var plaintext = args.Length == 2;
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
