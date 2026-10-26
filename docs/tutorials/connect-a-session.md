# Connect a session

This exercise connects the managed client to a local ZooKeeper ensemble,
synchronizes `/` and closes with server acknowledgment. It uses the source
checkout; no NuGet package has been released.

## Start the local servers

Install the pinned .NET SDK, Docker with Compose, and `just`. From the repository
root, run:

```sh
just zk-up 3.9.6
dotnet run --project samples/ZkLink.Samples.Basic -c Release -- \
  127.0.0.1:2181,127.0.0.1:2182,127.0.0.1:2183 --plaintext
```

Expect `State: Connected`, the negotiated timeout, and `Close: Acknowledged`.
The local fixture uses plaintext, so the example requires `--plaintext`.
Without that switch the sample requires TLS with platform trust and revocation
checks. Secure ports must be supplied explicitly.

The [sample source](../../samples/ZkLink.Samples.Basic/Program.cs) performs these
calls:

```csharp
var options = new ZooKeeperClientOptions
{
    ConnectionString = "127.0.0.1:2181,127.0.0.1:2182,127.0.0.1:2183",
    Tls = ZooKeeperTlsOptions.Disabled(), // Local plaintext fixture only.
};

await using ZooKeeperClient client =
    await ZooKeeperClient.ConnectAsync(options, cancellationToken);
await client.SyncAsync("/", cancellationToken);
await client.CloseAsync(cancellationToken);
```

`ConnectAsync` returns after handshake and configured authentication. `SyncAsync`
does not promise a globally fresh snapshot. `CloseAsync` succeeds after server
acknowledgment; disposal also releases local resources if an earlier call fails.

## Exercise digest authentication

The sample reads optional `ZKLINK_USER` and `ZKLINK_PASSWORD` environment variables.
Set both to disposable local test credentials and rerun the command. The sample's
explicit plaintext mode enables the unsafe plaintext-digest override. For an
application with TLS, configure credentials without that override:

```csharp
using ZooKeeperAuthentication authentication =
    ZooKeeperAuthentication.Digest(userName, password);
var options = new ZooKeeperClientOptions
{
    ConnectionString = "zk.example:2281",
    Authentication = [authentication],
};
await using ZooKeeperClient client =
    await ZooKeeperClient.ConnectAsync(options, cancellationToken);
await client.CloseAsync(cancellationToken);
```

The client takes its own credential copy synchronously. The configuration
credential can then be disposed independently; the client retains its copy for
authentication replay until terminal cleanup. Digest authentication alone does
not encrypt credentials or configure ACL authorization.

## Inspect session state

`client.Session` returns an immutable snapshot. To observe ordered transitions,
create a subscription and read it in a task your application owns and awaits:

```csharp
await using ZooKeeperStateSubscription subscription = client.SubscribeStateChanges();
await foreach (ZooKeeperSessionSnapshot state in
    subscription.ReadStatesAsync(cancellationToken))
{
    Console.WriteLine(state.State);
}
```

The stream starts with an atomic current snapshot. Canceling its enumeration
removes that subscription; it does not close the client. Do not await an entire
state stream before doing application work in the same execution path.

## Finish and verify both versions

Remove the local fixture when its data is disposable:

```sh
just zk-down 3.9.6
```

For an isolated smoke check on every node of both pinned server versions, run:

```sh
just zk-driver-matrix
```

This requires OpenSSL as well as Docker and .NET. It retains logs, checks digest
auth/sync/close, and cleans up its own servers. For behavior after disconnection,
cancellation and close failure, read the [connection reference](../reference/connections.md).
Continue with [Create, read, update and delete a node](use-node-operations.md).
Continue with [Execute a transaction](use-transactions.md). Watch delivery
awaits further implementation.

[Tutorials](README.md) · [Documentation home](../README.md)
