# Watch a node once

Start the local fixture with `just zk-up`. Set dedicated `ZKLINK_USER` and
`ZKLINK_PASSWORD` environment variables, as in the
[node tutorial](use-node-operations.md), then run:

```sh
dotnet run --project samples/ZkLink.Samples.Basic -c Release -- \
  127.0.0.1:2181 --plaintext --watches
```

The sample watches a unique missing path, creates an authenticated ephemeral,
reads exactly one buffered creation event, observes stream completion, deletes
the node and closes with acknowledgment. Expected output includes:

```text
Missing-node watch, buffered creation event and stream completion verified.
```

The same pattern with an already connected, authenticated `client` is:

```csharp
WatchedResult<ZNodeStat?> observed = await client.ExistsAndWatchAsync(path, token);
await using var watchLifetime = observed.Watch.ConfigureAwait(false);

// observed.Value is null when the node is missing; the watch is still active.
// Another operation or client can now create or change the node.
await foreach (ZooKeeperWatchEvent change in observed.Watch.ReadEventsAsync(token))
{
    // Restored events can arrive before application admission reopens.
    await client.WaitForConnectionAsync(token);
    // Reread application state; the event is an invalidation.
    ZNodeStat? current = await client.ExistsAsync(change.Path, token);
}
```

The event may already be buffered when enumeration starts. A one-shot stream
ends after its event; use a new watched read to rearm. State changes do not
consume the watch. Ending enumeration disposes the handle locally.

Use `GetDataAndWatchAsync` to obtain data/Stat with a data watch, or
`GetChildrenAndWatchAsync` for child names/parent Stat with a child watch. Unlike
the missing existence case, missing data/children reads throw `NoNode` and return
no handle.

`just zk-operations-matrix` runs the live tests and this sample on both pinned
versions, with retained logs and cleanup. See the [watch reference](../reference/watches.md)
for removal, cancellation, reconnect and queue limits. After the local exercise,
use `just zk-down` with the same selected version.

[Tutorials](README.md) · [Documentation home](../README.md)
