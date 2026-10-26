# Protocol boundaries and data ownership

ZkLink aims to implement the ZooKeeper client TCP protocol in managed C#.
The client owns one explicit session and at most one active transport, with
a serialized writer and ordered reader. Session management, bounded request
scheduling, TCP/TLS, node/ACL operations and typed transactions are implemented;
active watches and auxiliary APIs remain planned.

## Wire records and public results serve different lifetimes

The internal Jute codec describes bytes: big-endian values, strings, buffers,
vectors, and records. Framing places a length prefix around a bounded payload.
Those responsibilities do not determine how an application should own a result.

Public result wrappers copy incoming data and collections so a caller's later
mutation cannot change the snapshot. Data does not alias reusable transport
storage. Null data remains distinct from an empty byte buffer. Raw Stat fields
and signed versions are preserved so the wrapper does not silently reinterpret
server values.

The initial framing implementation uses owned arrays. Pooling would introduce
another lifetime to manage and needs allocation and ownership evidence before
adoption. Finite frame and collection bounds already limit allocations; these
are local safety choices rather than limits negotiated with a server.

## Failure types record evidence

A failed wait and a failed write are different facts. If request bytes may have
left the process and the response is missing, the caller cannot infer whether a
write took effect. Cancellation cannot undo that uncertainty.

The implemented failure contracts represent this distinction with `NotSent`,
`Rejected`, and `Unknown`. They preserve raw server error codes independently
from recognized enum values. Cancellation remains an `OperationCanceledException`
while exposing the same request evidence as ordinary failures.

These types deliberately provide no generic retryability flag. An application
decision depends on the operation and its outcome evidence. A repeated uncertain
write can have a different effect from the original request. The scheduler enforces these outcomes during send,
cancellation, late replies and disconnection. Virtual-time and controlled-stream
tests check those boundaries.

## Session ownership controls recovery

The session manager owns credentials, endpoint selection, liveness and connection
generations. The scheduler owns queued packets, positive request IDs and pending
reply order. A shared gate makes readiness, admission and state subscription
changes atomic. Socket I/O and user continuations run outside that gate.

Disconnect revokes admission before notifying callers. The old transport is
closed before reconnecting with the same credentials; sent application requests
are never replayed. Canceled sent requests retain ordered bookkeeping until a
reply or transport cleanup releases them. The writer retains its packet until
write completion even if a reply arrives first.

Terminal loss clears owned secrets and never creates a replacement session.
Applications choose a new client explicitly. The [connection reference](../reference/connections.md)
describes timeouts, state streams, security and close acknowledgment.

## Transaction replies carry two levels of evidence

An outer success header does not prove that a write transaction succeeded. The
scheduler examines all members before completing the caller: a nonzero member
error produces complete rollback diagnostics with the assigned request metadata.
No successful result list escapes from a rejected write transaction.

Read batches preserve independent data, child-name and error results. They do
not imply a shared snapshot. Both kinds capture the full request before returning
their task, and neither is replayed after an uncertain response loss.

## Metadata is separate from default diagnostics

Paths, endpoints, ACL identities, and payloads can be useful to inspect, but they
should not appear as an incidental consequence of formatting a model or
exception. Getter-only wrappers avoid generated content formatting, and default
exception messages omit request paths and endpoints. Explicit properties retain
the information for deliberate inspection. Code that logs those properties still
controls what it discloses.

For the implemented fields and constraints, consult
[Supporting public types](../reference/public-types.md). For the distinction
between contract tests and network behavior, read
[What the verification layers prove](verification.md).

[Explanation](README.md) · [Documentation home](../README.md)
