# Ephemeral nodes, descendant counts and identities

These methods on `ZooKeeperClient` use the current session, return `Task` and
accept a final optional `CancellationToken`. Each sends one request through the
existing bounded scheduler. Sent requests are never replayed automatically.

| Method | Result |
| --- | --- |
| `GetEphemeralsAsync(prefixPath)` | `IReadOnlyList<string>` of this session's ephemeral paths matching a textual prefix |
| `GetAllChildrenNumberAsync(path)` | `int` count of all descendants, excluding the queried node |
| `WhoAmIAsync()` | `IReadOnlyList<ZooKeeperClientIdentity>` of server-reported identities, with getter-only `Scheme` and `User` |

Exact nullable signatures are in the [API baseline](../../src/ZkLink/PublicAPI.Unshipped.txt).

## Ephemeral queries and chroot

The prefix is a valid absolute path, but need not name an existing node.
`/node` matches both `/node/child` and `/node2`; this is a textual match rather
than a subtree query. `/` lists the session's ephemerals in the configured
namespace. Other sessions' nodes and persistent nodes are excluded. Result order
is unspecified; no matches returns an empty list.

The configured chroot is prepended once to the request prefix. Returned paths
outside its component boundary are filtered, and the chroot is removed from the
remaining paths. For example, chroot `/app` cannot expose `/app2/node`, while
client prefix `/node` can still match `/app/node2` and return `/node2`.
Chroot is a namespace convenience; ACLs enforce authorization.

## Descendants and identities

Descendant counts include every depth, rather than just immediate children. A
leaf returns zero. The server requires READ permission on the queried node;
it does not perform a client-side traversal or filter the count according to
each descendant's ACL. Chroot `/` refers to the chroot node. Concurrent mutations
can change later observations; the count is not a reservation or snapshot of a
subsequent read.

`WhoAmIAsync` is bodyless and independent of chroot. Schemes and users are opaque
server values; configured credentials are not substituted into the result.
An unauthenticated connection can still have an `ip` identity. Empty identity
lists and empty user strings are preserved. Default identity formatting omits
values; explicitly reading or logging properties can expose sensitive data.

Both list results are immutable, independently owned snapshots, with no disposal
obligation. Collection counts and frame lengths obey the configured
[connection limits](connections.md#configuration).

## Errors and cancellation

Missing count paths raise `NoNode`; denied count paths raise `NoAuth`.
Server errors, including `Unimplemented` and unknown numeric codes, preserve
operation, request ID, endpoint and client-relative path metadata. Identity
queries have no path. There is no capability probe or fallback traversal.

Cancellation before transmission reports `NotSent`; after transmission it
reports `Unknown`, with late replies drained safely. Deadlines and disconnects
follow the same [request outcome rules](connections.md#state-recovery-and-request-outcomes)
as other operations.

## Runnable example and verification

With the local fixture running and dedicated `ZKLINK_USER` and `ZKLINK_PASSWORD`
environment variables set:

```sh
dotnet run --project samples/ZkLink.Samples.Basic -c Release -- \
  127.0.0.1:2181 --plaintext --auxiliary
```

The example creates an authenticated ephemeral, queries its prefix and zero
descendant count, checks for a digest identity, deletes the node and closes with
acknowledgment. It prints no identity values or credentials.

`just zk-operations-matrix` runs controlled live scenarios on ZooKeeper 3.8.7 and
3.9.6, including session isolation, textual prefixes, chroot, nested counts,
permission/missing-node errors and identities. The example runs on every node.
Controlled peers separately cover malformed replies, immutable results,
cancellation and late replies. These are plaintext local checks; live secure
servers, additional authentication schemes and broader fault qualification
remain open.

[Reference](README.md) · [Documentation home](../README.md)
