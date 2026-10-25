# Protocol boundaries and data ownership

ZkLink aims to implement the ZooKeeper client TCP protocol in managed C#.
Currently, the code supplies internal serialization and framing plus public
supporting contracts. Transport, sessions, request scheduling, and active watches
remain planned. The existing boundaries allow their data contracts to be checked
before a client starts making distributed requests.

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
write can have a different effect from the original request. Actual send,
recovery, and cancellation behavior still needs a transport and session
implementation; constructing an exception does not verify those behaviors.

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
