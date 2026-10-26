# Watch repeated changes

Build the repository and connect a client as described in
[Connect a session](connect-a-session.md). The authenticated sample exercises
one-shot, persistent and recursive delivery on a disposable node:

```sh
dotnet run --project samples/ZkLink.Samples.Basic -c Release -- \
  127.0.0.1:2181 --plaintext --watches
```

Set `ZKLINK_USER` and `ZKLINK_PASSWORD` to dedicated local test credentials first.
The sample verifies creation and a later data change through each persistent
mode in turn, removes the registrations, checks stream completion, deletes its node and
closes with acknowledgment. Its watch output is:

```text
One-shot, persistent and recursive watch delivery, removal and stream completion verified.
```

With an already connected `client` and a cancellation `token`, observe repeated
changes at one path:

```csharp
const string path = "/settings";
await using var watch = await client.AddWatchAsync(
    path, PersistentWatchMode.Persistent, token);

// Registration precedes the initial read; changes can already be buffered.
ZNodeStat? current = await client.ExistsAsync(path, token);
Console.WriteLine(current is null ? "Absent" : "Present");

await foreach (var change in watch.ReadEventsAsync(token))
{
    // ReconnectedWithGap requires a fresh read, just like a node invalidation.
    // Notifications may also arrive while restoration is still in progress.
    await client.WaitForConnectionAsync(token);
    current = await client.ExistsAsync(path, token);
    Console.WriteLine(current is null ? "Absent" : "Present");
}
```

A missing node is valid at registration. Deleting and recreating it does not end
the watch. Use `PersistentWatchMode.PersistentRecursive` for node changes at the
path and its descendants; recursive streams omit child-list duplicates. The default 3.8 compatibility profile requires one persistent mode per exact
path; the 3.9 profile allows independent modes there. Each
handle permits one consumer. Ending enumeration removes the handle locally.

A reconnect gap means changes may have been missed. Events provide invalidation,
not a history; a tree consumer must resnapshot its relevant subtree. Slow
consumers can exhaust the shared event budget and fault the client explicitly.
See the [watch reference](../reference/watches.md) for limits and removal results.

Run `just zk-operations-matrix` to verify both pinned server versions, including
same-session restoration and gap reporting after a real connection cut.

[Tutorials](README.md) · [Documentation home](../README.md)
