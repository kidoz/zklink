# TTL node creation

`CreateAsync` and `MultiAsync` support `PersistentWithTtl` and
`PersistentSequentialWithTtl`. Set `ZooKeeperClientOptions.EnableTtlNodes = true`
before connecting. It defaults to false and is captured with the other settings.
Every server endpoint must also have `zookeeper.extendedTypesEnabled=true`.
The client setting is an operator assertion; it does not configure servers or
probe their capabilities.

Without client opt-in, TTL creates throw `NotSupportedException` locally. A
transaction containing a TTL create is rejected before any member is sent.
Constructing a TTL operation descriptor is allowed without a connected client;
the submitting client enforces the setting. With client opt-in against a server
that disables TTL, the server's `Unimplemented` error is preserved. A rejected
write transaction commits none of its members and retains full diagnostics.
There is no fallback to ordinary persistent creation or automatic retry.

## Create settings and results

`CreateOptions` requires an explicit ACL list and positive whole milliseconds,
up to `CreateOptions.MaxTtlMilliseconds` (`2^40 - 1`). Missing, zero, negative,
fractional-millisecond and excessive TTL values are rejected; ordinary modes
reject a supplied TTL. The exact integer value is sent without rounding.

With an authenticated `client` connected using `EnableTtlNodes = true`:

```csharp
var ttlOptions = new CreateOptions(
    [new(ZooKeeperPermissions.All, "auth", "")],
    CreateMode.PersistentWithTtl,
    TimeSpan.FromSeconds(30));

CreateResult created = await client.CreateAsync("/ttl-example", null, ttlOptions);
await client.DeleteAsync(created.Path, ZNodeVersion.Any);

var sequentialOptions = new CreateOptions(
    ttlOptions.Acl,
    CreateMode.PersistentSequentialWithTtl,
    TimeSpan.FromSeconds(30));

IReadOnlyList<ZooKeeperWriteResult> results = await client.MultiAsync(
    [ZooKeeperWriteOperation.Create("/ttl-sequence-", null, sequentialOptions)]);
string actualPath = ((ZooKeeperCreatedResult)results[0]).Value.Path;
await client.DeleteAsync(actualPath, ZNodeVersion.Any);
```

Both modes return the actual client-relative path and Stat in one response.
Sequential suffixes are preserved, including through chroot and transactions.
Inputs use the existing synchronous snapshot and request-budget rules. Sent
operations are never replayed; cancellation after transmission can leave an
`Unknown` outcome, including a sequential create that actually committed.

## Cleanup semantics

TTL nodes are persistent extended types, not session ephemerals. Closing a
session does not delete them, and they may have children. An empty node becomes
eligible for asynchronous server cleanup after its TTL since the last data
modification. Data writes update that server timestamp. Children prevent cleanup
until the node becomes empty.

TTL is not a lease, an exact deletion timer, or proof that a node has disappeared.
Observe node existence explicitly when it matters. Stat fields retain the raw
server response; a nonzero `EphemeralOwner` alone is not a reliable node-type
test. Container nodes also use asynchronous cleanup: a previously used empty
container parent may disappear before the next child creation, which then fails
with `NoNode`.

## Local verification

Run `just zk-ttl-matrix`. It creates isolated three-node fixtures with TTL disabled
and enabled for both ZooKeeper 3.8.7 and 3.9.6. Each verifies exact versions and
authenticated readiness, retains configuration/server/test logs, and removes its
containers and volumes. Required infrastructure or configuration failures are
failures, not skipped tests. The ordinary `just zk-operations-matrix` remains a
separate suite on TTL-disabled servers.

The base [Compose file](../../compose.yaml) explicitly disables extended types.
The test-only [TTL override](../../compose.ttl.yaml) enables them on every node
and scans for cleanup every second. For a manual local fixture:

```sh
docker compose -f compose.yaml -f compose.ttl.yaml \
  --env-file dev/zookeeper-3.9.6.env up -d --wait --wait-timeout 90
# Exercise the client, then clean up using the same files and version.
docker compose -f compose.yaml -f compose.ttl.yaml \
  --env-file dev/zookeeper-3.9.6.env down --volumes
```

Live checks cover both modes, single and atomic creation, maximum TTL, chroot,
path/stat/data results, survival after acknowledged close, child protection,
eventual cleanup, disabled-server rejection and transaction rollback. Controlled
peers also cover exact minimum/maximum encoding, input capture, local limits,
cancellation and late replies. Cleanup tests use bounded polling and do not
assert an exact deletion time. These are local plaintext checks; live TLS,
broader faults and release qualification remain open.

[Reference](README.md) · [Documentation home](../README.md)
