# Node and ACL operations

All methods below are on `ZooKeeperClient`, return `Task` and accept a final
optional `CancellationToken`. They use the current session and never replay sent
operations automatically. Exact signatures are in the
[API baseline](../../src/ZkLink/PublicAPI.Unshipped.txt).

| Method | Result and behavior |
| --- | --- |
| `ExistsAsync(path)` | `ZNodeStat?`; returns null only for `NoNode`; registers no watch |
| `GetDataAsync(path)` | `ZNodeData`; owned nullable bytes and Stat; null differs from empty |
| `GetChildrenAsync(path)` | `ZNodeChildren`; child names and parent Stat in one response; order is unspecified |
| `CreateAsync(path, data, options)` | `CreateResult`; actual client-relative path and Stat from one response; ACLs are explicit |
| `SetDataAsync(path, data, version)` | Updated `ZNodeStat`; condition uses the data `Version` |
| `DeleteAsync(path, version)` | Deletes one node using its data version; nonempty nodes fail |
| `GetAclAsync(path)` | `ZNodeAcl`; scheme-aware ACL entries and Stat, including `Aversion` |
| `SetAclAsync(path, acl, aclVersion)` | Updated `ZNodeStat`; condition uses ACL `Aversion` |

`ZNodeVersion.Any` encodes the server wildcard `-1`. `Exact(int)` preserves signed
versions, including overflow; default `ZNodeVersion` means exact zero. Child
changes affect `Cversion`, data changes affect `Version`, and ACL changes affect
`Aversion`. They are independent counters.

## Create modes

Persistent, ephemeral, persistent-sequential, ephemeral-sequential and container
modes are implemented. Ordinary creates use `create2`; container creates use
`createContainer`. Both return path and Stat without a follow-up read. Sequential
suffixes are returned exactly as received; they do not promise a globally unique
or permanently positive counter.

Ephemeral nodes belong to the session and cannot have children. Acknowledged
close has been tested to remove them. Disconnection or unacknowledged close alone
does not prove removal. Empty containers can be removed asynchronously by the
server; a subsequent child create can therefore fail with `NoNode`.

TTL creation requires `EnableTtlNodes = true` on the client and extended node
types enabled on every server endpoint. Otherwise it fails locally or preserves
the server rejection, without probing or fallback. Both TTL modes work in single
creates and transactions. See [TTL node creation](ttl-nodes.md) for bounds and
eventual cleanup semantics.

## Paths, ownership and limits

Every path is validated and translated through the configured chroot. Create
results have that prefix removed at component boundaries; child names stay
unchanged. `/` names the chroot node. Methods do not create missing parents,
normalize paths, delete recursively or install watches.

Data and ACL inputs are encoded synchronously before the method returns its
task, so later caller mutation cannot alter the packet. Queue/byte capacity is
reserved before allocating the owned encoded packet. Results use independent
owned snapshots. The [connection limits](connections.md#configuration) constrain
requests and replies independently of server Jute limits.

## Errors and ACL permissions

Only `ExistsAsync` converts `NoNode` to null. Other failures preserve their numeric
server code, recognized enum, operation, request ID, safe endpoint metadata and
client-relative path. Common definitive rejections include `NoNode`, `NodeExists`,
`BadVersion`, `NotEmpty`, `NoChildrenForEphemerals`, `NoAuth` and `InvalidAcl`.
Cancellation, deadlines and disconnects preserve the existing
[request outcomes](connections.md#state-recovery-and-request-outcomes).

ACLs do not inherit. Create/delete permission is checked on the parent; read/write
permission applies to the node; admin permission controls ACL replacement.
An `auth` ACL is expanded by the server to authenticated identities. Digest
authentication and a stored digest ACL identity are different inputs.

On both tested patches, `GetAclAsync` is allowed with read or admin permission.
The server masks digest identity hashes for readers without admin permission;
ZkLink preserves that response. Server validation order can also return
`InvalidAcl` before a missing parent-create permission is considered. A node's
`NoAuth` response does not end an otherwise valid session.

## Verification scope

The live suite covers both ZooKeeper 3.8.7 and 3.9.6: CRUD, missing nodes, version
conflicts, null/empty data, the default 1,000,000-byte data limit, create modes,
chroot, ACL permission/version behavior and ephemeral deletion after acknowledged
close. Controlled peers additionally check synchronous input capture, local
size rejection, late replies and cancellation outcomes.

Run `just zk-operations-matrix` for isolated live servers, retained evidence and
cleanup. For session ephemerals, descendant counts and identities, see
[auxiliary operations](auxiliary-operations.md). These are plaintext tests. Persistent/recursive watches,
live secure ZooKeeper and broader fault qualification remain open.

[Reference](README.md) · [Documentation home](../README.md)
