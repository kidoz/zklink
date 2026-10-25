# Create, read, update and delete a node

Use the source checkout, pinned .NET SDK, Docker with Compose, `just` and OpenSSL.
The sample uses a unique ephemeral node and an explicit ACL for its authenticated
creator. It writes only to the disposable local fixture in this exercise.

## Run the example

From the repository root:

```sh
just zk-up 3.9.6
export ZKLINK_USER=zklink-local-example
export ZKLINK_PASSWORD="$(openssl rand -hex 24)"
dotnet run --project samples/ZkLink.Samples.Basic -c Release -- \
  127.0.0.1:2181,127.0.0.1:2182,127.0.0.1:2183 --plaintext --nodes
unset ZKLINK_USER ZKLINK_PASSWORD
just zk-down 3.9.6
```

Expect `Node create/read/update/delete completed.` and `Close: Acknowledged`.
The password is disposable and is not printed. `--plaintext` explicitly selects
the local development fixture and its unsafe plaintext-digest override. Use TLS
for protected credentials outside this fixture.

## Understand the calls

The compiled [sample](../../samples/ZkLink.Samples.Basic/Program.cs) connects with
digest authentication, then performs:

```csharp
var createOptions = new CreateOptions(
    [new ZooKeeperAcl(ZooKeeperPermissions.All, "auth", "")],
    CreateMode.Ephemeral);

CreateResult created = await client.CreateAsync(
    path, "initial"u8.ToArray(), createOptions, cancellationToken);
ZNodeData current = await client.GetDataAsync(created.Path, cancellationToken);
ZNodeStat updated = await client.SetDataAsync(
    created.Path, "updated"u8.ToArray(),
    ZNodeVersion.Exact(current.Stat.Version), cancellationToken);
await client.DeleteAsync(
    created.Path, ZNodeVersion.Exact(updated.Version), cancellationToken);
```

The `auth` ACL is expanded by ZooKeeper to the authenticated creator. The data
version makes the update conditional on the value read earlier. Another write
can cause `BadVersion`; a missing response can leave `Unknown`. Neither case
causes an automatic retry.

The sample deletes its node explicitly. If an operation fails, disposal closes
the client; because the node is ephemeral, the server also removes it when that
session ends. An unacknowledged close can leave it present until actual expiry.

## Verify the operation matrix

```sh
just zk-operations-matrix
```

This starts isolated three-node ensembles for both pinned versions, verifies
exact server versions and readiness, runs the .NET node/ACL tests, and runs the
CRUD sample on each node. It prints the evidence directory and removes its own
containers and volumes on success or failure. Missing prerequisites or test
configuration fail the command rather than skipping checks.

For the full method and error contracts, use the [node reference](../reference/nodes.md).
For connection ownership and cancellation, use the [session reference](../reference/connections.md).

[Tutorials](README.md) · [Documentation home](../README.md)
