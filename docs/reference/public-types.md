# Supporting public types

All types below are in namespace `ZkLink`. They are implemented supporting
contracts and have not shipped in a package. The current client exposes connection/session and node/ACL methods; watch-removal
values still await their operation APIs. Node result wrappers are returned by the
[node operations](nodes.md).

Exact signatures and nullable annotations are in the
[unshipped API baseline](../../src/ZkLink/PublicAPI.Unshipped.txt). Source links
below provide constructor validation and XML documentation.

## Connection and session types

`ZooKeeperClient`, `ZooKeeperClientOptions`, `ZooKeeperTlsOptions`,
`ZooKeeperAuthentication`, `ZooKeeperCompatibilityProfile`,
`ZooKeeperSessionSnapshot`, `ZooKeeperSessionState`, `ZooKeeperCloseStatus` and
`ZooKeeperStateSubscription` form the current connection API. See the
[connection reference](connections.md) for their behavior and defaults.

`ZooKeeperConnectionException` reports a safe `ZooKeeperConnectionFailure`,
optional endpoint and server code during establishment. `ZooKeeperSessionException`
reports session state and optional server code. `ZooKeeperEventOverflowException`
identifies a terminal state-event gap. These describe connection/session failures
without inventing an application request.

## Endpoints and create settings

| Type | Contract |
| --- | --- |
| [ZooKeeperEndpoint](../../src/ZkLink/Configuration/ZooKeeperEndpoint.cs) | Getter-only `Host` and `Port`; default port 2181. Host is a DNS name or unbracketed IP literal without credentials, URI syntax, or chroot. Port range is 1–65535. |
| [CreateOptions](../../src/ZkLink/Configuration/CreateOptions.cs) | Copies an explicit, nonempty ACL list; defaults to `CreateMode.Persistent`. TTL is required only for TTL modes and must be positive whole milliseconds, at most `MaxTtlMilliseconds` = 2^40−1. Construction performs no network operation. |
| [CreateMode](../../src/ZkLink/Types/CreateMode.cs) | Discrete values: `Persistent`, `Ephemeral`, `PersistentSequential`, `EphemeralSequential`, `Container`, `PersistentWithTtl`, `PersistentSequentialWithTtl`. Values are not flags. |

## ACLs and identities

| Type | Contract |
| --- | --- |
| [ZooKeeperAcl](../../src/ZkLink/Security/ZooKeeperAcl.cs) | Getter-only `Permissions`, `Scheme`, and `Id`; scheme-aware data, independent of authentication configuration. `OpenUnsafe` explicitly grants all permissions to `world:anyone`. |
| [ZooKeeperPermissions](../../src/ZkLink/Security/ZooKeeperPermissions.cs) | Flags: `None=0`, `Read=1`, `Write=2`, `Create=4`, `Delete=8`, `Admin=16`, `All=31`. Child create/delete permissions apply to the parent. |
| [ZooKeeperClientIdentity](../../src/ZkLink/Types/ZooKeeperClientIdentity.cs) | Getter-only `Scheme` and `User` for an authenticated identity. No `whoAmI` operation is implemented yet. |

## Node snapshots and versions

| Type | Contract |
| --- | --- |
| [ZNodeData](../../src/ZkLink/Types/ZNodeData.cs) | Copies nullable byte data; preserves null versus empty. Exposes `ReadOnlyMemory<byte>? Data` and `Stat`. |
| [ZNodeChildren](../../src/ZkLink/Types/ZNodeChildren.cs) | Copies child names into a read-only collection with parent `Stat`; names are not absolute paths and order is unspecified. |
| [ZNodeAcl](../../src/ZkLink/Types/ZNodeAcl.cs) | Copies ACL entries into a read-only collection and exposes associated `Stat`, including `Aversion`. |
| [CreateResult](../../src/ZkLink/Types/CreateResult.cs) | Getter-only `Path` and `Stat`; preserves the returned sequence suffix. |
| [ZNodeStat](../../src/ZkLink/Types/ZNodeStat.cs) | Readonly record struct preserving eleven raw fields in schema order. |
| [ZNodeVersion](../../src/ZkLink/Types/ZNodeVersion.cs) | Readonly record struct with `Value`, `Any`, and `Exact(int)`. Default is exact zero; `Exact(-1)` has the same wildcard wire meaning as `Any`. Other signed values are preserved. |

`ZNodeStat` fields, in order, are `Czxid`, `Mzxid`, `Ctime`, `Mtime`, `Version`,
`Cversion`, `Aversion`, `EphemeralOwner`, `DataLength`, `NumChildren`, and `Pzxid`.
Times remain raw server milliseconds. `EphemeralOwner` retains raw bits without
inferring the node type.

Snapshot constructors reject null collection entries. Current internal safety
ceilings are 64 MiB for copied byte data and 1,000,000 collection entries. These
are local allocation limits, not negotiated server capabilities.

## Request failures

| Type | Contract |
| --- | --- |
| [ZooKeeperOperation](../../src/ZkLink/Types/ZooKeeperOperation.cs) | Named operation identifiers with numeric wire opcodes; identifiers do not establish executable operations. |
| [ZooKeeperRequestInfo](../../src/ZkLink/Exceptions/ZooKeeperRequestInfo.cs) | `Operation`, derived `Opcode`, optional positive `RequestId`, optional `Endpoint`, and optional client-relative `Path`. Request ID is null before assignment. |
| [IZooKeeperRequestFailure](../../src/ZkLink/Exceptions/IZooKeeperRequestFailure.cs) | Common `Request`, `Outcome`, nullable numeric `ErrorCode`, and nullable `KnownErrorCode`. Unknown numeric server codes remain inspectable. |
| [ZooKeeperException](../../src/ZkLink/Exceptions/ZooKeeperException.cs) | Base request failure with explicit evidence. Code zero is invalid; `Rejected` requires a server code; `NotSent` forbids one. |
| [ZooKeeperBackpressureException](../../src/ZkLink/Exceptions/ZooKeeperBackpressureException.cs) | Local capacity failure: always `NotSent`, with no server code. |
| [ZooKeeperTimeoutException](../../src/ZkLink/Exceptions/ZooKeeperTimeoutException.cs) | Local deadline failure with `NotSent` or `Unknown` outcome and no server error. |
| [ZooKeeperCloseException](../../src/ZkLink/Exceptions/ZooKeeperCloseException.cs) | Requires `CloseSession`; `CloseAcknowledged` is false. |
| [ZooKeeperOperationCanceledException](../../src/ZkLink/Exceptions/ZooKeeperOperationCanceledException.cs) | Derives from `OperationCanceledException` and implements the failure interface. Preserves the token; outcome is `NotSent` or `Unknown`; no server error. `CloseAcknowledged` is false. |
| [ZooKeeperMultiException](../../src/ZkLink/Exceptions/ZooKeeperMultiException.cs) | Rejected `Multi` with copied, complete ordered diagnostics and a zero-based `FailedMemberIndex` pointing to a nonzero server error. |
| [ZooKeeperMultiDiagnostic](../../src/ZkLink/Exceptions/ZooKeeperMultiDiagnostic.cs) | Member `Index`, raw `Opcode`, raw `ErrorCode`, recognized error when known, optional `Path` and `Stat`. Success-looking entries do not establish commitment. |
| [ZooKeeperErrorCode](../../src/ZkLink/Exceptions/ZooKeeperErrorCode.cs) | Recognized server failure codes; not a retry policy. |

[RequestOutcome](../../src/ZkLink/Types/RequestOutcome.cs) has three values:

| Value | Meaning |
| --- | --- |
| `NotSent` | No request bytes were handed to the transport. |
| `Rejected` | A definitive server response establishes rejection. |
| `Unknown` | Bytes may have left the client without a definitive result. |

Default exception text omits paths and endpoints. Payload, ACL, and identity
wrappers use type-name formatting rather than formatting their contents.
Explicit property inspection can still expose those values.

## Watch-removal values

| Type | Contract |
| --- | --- |
| [WatchRemovalMode](../../src/ZkLink/Types/WatchRemovalMode.cs) | `Local` or `ServerConfirmed`, selecting the intended acknowledgment level. |
| [WatchRemovalStatus](../../src/ZkLink/Types/WatchRemovalStatus.cs) | `LocalOnly`, `ServerConfirmed`, `NoWatcher`, or `SharedRegistrationRetained`. Shared retention means another logical handle still needs the server registration. |
| [ZooKeeperWatchRemovalResult](../../src/ZkLink/Watches/ZooKeeperWatchRemovalResult.cs) | Getter-only positive `RegistrationId` and validated `Status`. |

No watch handle, event stream, or removal operation is implemented yet.

[Reference](README.md) · [Documentation home](../README.md)
