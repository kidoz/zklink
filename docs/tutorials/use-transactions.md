# Execute a transaction

Use the pinned .NET SDK, Docker with Compose, `just` and OpenSSL. This exercise
uses the source checkout and an authenticated ephemeral node in the local fixture.

```sh
just zk-up 3.9.6
export ZKLINK_USER=zklink-local-example
export ZKLINK_PASSWORD="$(openssl rand -hex 24)"
dotnet run --project samples/ZkLink.Samples.Basic -c Release -- \
  127.0.0.1:2181,127.0.0.1:2182,127.0.0.1:2183 --plaintext --transactions
unset ZKLINK_USER ZKLINK_PASSWORD
just zk-down 3.9.6
```

Expect `Transaction create/update/check/read/delete completed.` and
`Close: Acknowledged`. Plaintext and its unsafe digest override are explicitly
selected for this local fixture. The generated password is disposable and is
not printed.

## Build an atomic write

The compiled [sample](../../samples/ZkLink.Samples.Basic/Program.cs) submits:

```csharp
var options = new CreateOptions(
    [new ZooKeeperAcl(ZooKeeperPermissions.All, "auth", "")],
    CreateMode.Ephemeral);

IReadOnlyList<ZooKeeperWriteResult> results = await client.MultiAsync([
    ZooKeeperWriteOperation.Create(path, "initial"u8.ToArray(), options),
    ZooKeeperWriteOperation.SetData(path, "updated"u8.ToArray(), ZNodeVersion.Exact(0)),
    ZooKeeperWriteOperation.Check(path, ZNodeVersion.Exact(1)),
], cancellationToken);
```

The server creates the node, updates it and checks the resulting version as one
atomic transaction. Results follow the input order. The create result is a
`ZooKeeperCreatedResult`, the update result is a `ZooKeeperDataSetResult`, and
the check result is a `ZooKeeperCheckedResult`.

If any member is rejected, catch `ZooKeeperMultiException` and inspect its
`FailedMemberIndex` and complete `Diagnostics`. None of that transaction's members
committed. Keep supplied data stable while invoking `MultiAsync`; it captures
the packet before returning its task.

## Read and delete

```csharp
IReadOnlyList<ZooKeeperReadResult> read = await client.MultiReadAsync([
    ZooKeeperReadOperation.GetData(path),
], cancellationToken);

await client.MultiAsync([
    ZooKeeperWriteOperation.Delete(path, ZNodeVersion.Exact(1)),
], cancellationToken);
```

Read batches can contain successful results and member errors. A batch of reads
does not promise an atomic snapshot. The sample deletes explicitly; its ephemeral
node also belongs to the session if an earlier call fails.

A missing response differs from a server rejection: the client reports `Unknown`
and does not replay the transaction. Do not assume that repeating an uncertain
sequential create is safe.

Run `just zk-operations-matrix` to verify success, rollback, dropped-response
behavior and the sample on both pinned server versions. For all result types
and limits, see the [transaction reference](../reference/transactions.md).

[Tutorials](README.md) · [Documentation home](../README.md)
