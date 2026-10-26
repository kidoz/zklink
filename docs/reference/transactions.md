# Transactions and read batches

`MultiAsync` submits a nonempty atomic write transaction and returns
`IReadOnlyList<ZooKeeperWriteResult>` in input order. `MultiReadAsync` submits a
nonempty read batch and returns `IReadOnlyList<ZooKeeperReadResult>` in input
order. Both accept a final optional `CancellationToken`.

## Operation descriptors

| Factory | Meaning |
| --- | --- |
| `ZooKeeperWriteOperation.Create(path, data, options)` | Create with explicit ACLs and a supported create mode |
| `ZooKeeperWriteOperation.SetData(path, data, version)` | Update using the data version condition |
| `ZooKeeperWriteOperation.Delete(path, version)` | Delete using the data version condition |
| `ZooKeeperWriteOperation.Check(path, version)` | Check the node's data version inside the transaction |
| `ZooKeeperReadOperation.GetData(path)` | Read nullable data and Stat without a watch |
| `ZooKeeperReadOperation.GetChildren(path)` | Read child names without a watch or parent Stat |

The operation hierarchies are closed: arbitrary opcode bodies, read/write mixing,
nested transactions and watched reads cannot be supplied through these types.
Descriptors expose `Path` and `Operation`. Multi-read child names use
`ZooKeeperOperation.GetChildrenNames` (opcode 8); the standalone children method
uses opcode 12 and includes parent Stat.

Factories retain supplied data memory. Keep that memory stable until `MultiAsync`
returns its task. The invocation synchronously validates the full batch, measures
its encoded size, reserves capacity, translates paths and encodes independent
packets before returning. Afterwards, changing the input list or data cannot
alter the submitted transaction. No entire intermediate member list or payload
copy is allocated before byte admission.

Empty lists, null members, invalid paths and oversized batches fail locally.
Combined frame size includes every member header, data, ACL and chroot prefix.
Create modes follow the [node reference](nodes.md#create-modes); TTL creation is
still unavailable. See [connection limits](connections.md#configuration).

## Typed results

The outer result lists are read-only. Returned data and collections are owned
snapshots.

| Write result | Value |
| --- | --- |
| `ZooKeeperCreatedResult` | `Value`: actual client-relative path and Stat |
| `ZooKeeperDataSetResult` | `Stat`: resulting node Stat |
| `ZooKeeperDeletedResult` | Successful deletion |
| `ZooKeeperCheckedResult` | Successful version check |

| Read result | Value |
| --- | --- |
| `ZooKeeperDataReadResult` | `Value`: `ZNodeData`, preserving null versus empty |
| `ZooKeeperChildrenReadResult` | `Value`: child names, with unspecified order and no parent Stat |
| `ZooKeeperReadErrorResult` | Original `ErrorCode` and optional `KnownErrorCode` |

Match each read result to its input index. A missing node or denied read can
produce an error beside successful members. Read batches do **not** provide an
atomic snapshot or a globally fresh view.

## Rejection and uncertainty

A successful write transaction commits all members. Later members can refer to
nodes or versions established by earlier members in the same transaction.

A definitively rejected write transaction commits no members and throws
`ZooKeeperMultiException`. Its `FailedMemberIndex` identifies the first nonzero
member error. `Diagnostics` contains the complete ordered member list, including
raw opcodes/errors and any returned client-relative path or Stat. Zero errors
and other success-looking diagnostics inside that exception are not committed
results. Later members may report `RuntimeInconsistency` after the actual failure.
Unknown numeric codes remain inspectable.

An error in the outer reply can lack member diagnostics and uses the ordinary
request exception. A malformed response faults the connection rather than
inventing successful members. Session-ending member errors revoke readiness
before the observed result completes; `NoAuth` alone does not end the session.

Cancellation before send reports `NotSent`. Cancellation or missing reply after
transmission begins reports `Unknown`, because the transaction may have committed.
Sent canceled requests retain their expected result shape so a late response can
be drained safely. Reconnect never automatically replays the transaction.

## Verification

Both pinned ZooKeeper versions have live tests for typed success, rollback at
each failing position, chroot/sequential results, extended create modes and
independent read errors. A bounded proxy forwards a real transaction, drops its
reply and permits reconnect; tests verify actual commitment, `Unknown`, unchanged
session identity and exactly one forwarded transaction.

Controlled peers cover input capture, size rejection, outer/member errors,
cancellation and malformed placeholders. The Gradle-managed Apache oracle checks
the existing multi wire fixtures in both directions. Run `just zk-operations-matrix`
for live checks and the sample on all six nodes. These are plaintext tests;
broader fault, secure-server, Native AOT and release qualification remain open.

[Reference](README.md) · [Documentation home](../README.md)
