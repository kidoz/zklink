# Connection and session reference

The connection methods on `ZooKeeperClient` are `ConnectAsync`, `Session`,
`SubscribeStateChanges`, `WaitForConnectionAsync`, `UpdateEndpointsAsync`,
`SyncAsync`, `CloseAsync` and `DisposeAsync`. Network methods return `Task` and
accept a final `CancellationToken`. Exact declarations are in the
[API baseline](../../src/ZkLink/PublicAPI.Unshipped.txt).

Node CRUD, children/stat and ACL methods are described in the [node reference](nodes.md).
Session ephemerals, descendant counts and identities are described in the
[auxiliary reference](auxiliary-operations.md).

## Configuration

`ConnectAsync` validates and snapshots options, credentials and certificate
handles synchronously before returning its task. Settings are fixed for that
client, except that endpoint updates affect future connection attempts.

| Setting | Default and constraints |
| --- | --- |
| `ConnectionString` | Required `host[:port],host[:port][/chroot]`; bracket IPv6; default port 2181, including when TLS is selected |
| `Chroot` | Optional; must agree with an embedded chroot; `/` means no prefix |
| `Tls` | `ZooKeeperTlsOptions.Required()`; secure ports must be explicit |
| `Authentication` | Empty; at most 32 digest entries, each frame-bounded; combined credentials at most 32 MiB |
| `AllowUnsafePlaintextDigest` | False; explicit opt-in required for digest over plaintext |
| `CompatibilityProfile` | `ZooKeeper38`; `ZooKeeper39` is an operator assertion, not negotiated discovery |
| `SessionTimeout` | 30 seconds requested; actual negotiated timeout governs liveness |
| `InitialConnectTimeout` | 30 seconds across endpoint attempts |
| `ConnectAttemptTimeout` | 5 seconds, reduced by remaining initial/recovery time |
| `OperationTimeout` | 30 seconds from invocation, including queue time |
| `CloseTimeout` | 5 seconds to obtain acknowledgment before cleanup |
| `MaxInFlightRequests` | 256; range 1–65,536; canceled sent requests retain their slots |
| `MaxQueuedRequests` | 1,024; range 1–1,000,000; full queue fails immediately |
| `MaxPendingRequestBytes` | 32 MiB; range 8 bytes–1 GiB; owned encoded application packets |
| `MaxFrameBytes` | 1,048,575, excluding four-byte prefix; range 45 bytes–64 MiB |
| `MaxDataBytes` | 1,000,000; range 0–64 MiB, additionally constrained by frame size |
| `MaxCollectionElements` | 100,000; range 1–1,000,000, additionally constrained by frame size |
| `MaxBufferedEvents` | 4,096 globally after fan-out; range 1–1,000,000; also bounds subscription count |

Timeouts require positive whole milliseconds, at most `int.MaxValue` milliseconds.
Frame/data limits do not change the server's Jute limits. Chroot is namespace
translation; it does not provide ACL isolation or create the chroot node.

## TLS and authentication

TLS completes before the ZooKeeper handshake. `Required` uses platform-selected
protocols, chain validation, the configured endpoint host as TLS identity and
online revocation by default. It never falls back to plaintext. An optional
`serverName` overrides the identity explicitly; resolving a host to an IP does
not change it. Custom trust roots retain hostname validation. At most 256 roots
are allowed. There is no general accept-any-certificate callback.

`Required` accepts an application-owned client certificate with a private key and
optional custom roots. Connect captures independent certificate handles; client
cleanup does not dispose the application's originals. Revocation and certificate
stores depend on the platform. Local loopback evidence currently covers macOS
arm64 with ephemeral roots and revocation disabled for those test certificates;
live ZooKeeper TLS/mTLS and other platforms remain unqualified.

`ZooKeeperAuthentication.Digest(user, password)` owns UTF-8 `user:password` bytes,
rejects colons in the username and limits the encoded credential to 1,000,000
bytes. Dispose the configuration object when no longer needed. The client owns
its captured copy until terminal cleanup and clears it then. Multiple auth packets
are acknowledged sequentially and replayed before readiness after reconnect.
Authentication failure is terminal; an operation's `NoAuth` does not by itself
end the session. SASL and read-only mode are unavailable.

## State, recovery and request outcomes

Snapshots expose `State`, diagnostic `SessionId`, requested/negotiated timeout,
`ConnectionGeneration`, `LastReplyZxid`, `CloseStatus` and optional `ErrorCode`.
They never expose the session password.

| State | Meaning |
| --- | --- |
| `Connecting`, `Authenticating`, `Restoring` | Establishment before application readiness; restoration currently has no watches to restore |
| `Connected` | Application admission is open |
| `Disconnected` | Admission is revoked while the same session is being recovered |
| `Expired` | Server-confirmed expiry; terminal |
| `Lost` | Local abandonment after the negotiated timeout without valid session traffic; terminal |
| `AuthFailed`, `Faulted` | Terminal authentication or local protocol/event failure |
| `Closing`, `Closed` | Admission stopped; close in progress or local resources released |

Endpoints and usable DNS addresses are randomized. Failed endpoint cycles use
jittered backoff capped by remaining time. The old transport closes before a new
one is used. Reconnect retains the session ID/password and reply watermark;
terminal loss never silently creates a replacement session.

Idle ping is approximately one third of negotiated timeout `T`. A silent
transport fails after `2T/3`; at `T` the driver abandons recovery as `Lost`.
Outgoing data does not reset inbound liveness. Local abandonment can occur while
the server still retains the session, so its ephemerals can remain until expiry.
An unanswered application request also forces recovery after its operation
deadline plus `T`, even when ping acknowledgments continue.

New requests fail immediately while disconnected. Call `WaitForConnectionAsync`
explicitly before submitting new work. Sent operations are never replayed
automatically. `SyncAsync` validates and translates its path; it does not establish
a globally fresh snapshot or expose node data.

| Failure evidence | Outcome |
| --- | --- |
| Queue/byte limit, queued cancellation/timeout, unsent disconnect | `NotSent` |
| Definitive server rejection | `Rejected` |
| Transmission started and result is missing, or server error is ambiguous | `Unknown` |

Cancellation throws `ZooKeeperOperationCanceledException`, remains ordinary task
cancellation and preserves the outcome. Deadlines throw `ZooKeeperTimeoutException`.
After sent cancellation/timeout the driver retains an ordered entry to drain a
late reply; it still counts toward limits. Positive request IDs wrap without
colliding with pending IDs, and an old connection cannot complete new work.

Each state subscription has one consumer, starts with an atomic snapshot and
then yields ordered transitions. Disposing/canceling it is local. Exhausting the
shared event budget faults the client explicitly with
`ZooKeeperEventOverflowException`; transitions are not silently dropped.

## Closing

`CloseAsync` stops admission, ends outstanding caller waits consistently, sends
`closeSession` when possible and awaits acknowledgment within its deadline.
Success records `Acknowledged`; already confirmed expiry needs no close packet
and records `SessionExpired`. Otherwise close throws `ZooKeeperCloseException`
or cancellation after local cleanup, recording `Unacknowledged`. Repeated calls
return the first close task/result. Terminal failure states remain visible after
disposal.

`DisposeAsync` always releases local resources and does not require acknowledgment.
Unacknowledged close does not prove that the server removed ephemerals.

[Reference](README.md) · [Documentation home](../README.md)
