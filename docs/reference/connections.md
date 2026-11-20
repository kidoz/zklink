# Connection and session reference

The connection methods on `ZooKeeperClient` are `ConnectAsync`, `Session`,
`SubscribeStateChanges`, `WaitForConnectionAsync`, `UpdateEndpointsAsync`,
`SyncAsync`, `CloseAsync` and `DisposeAsync`. Network methods return `Task` and
accept a final `CancellationToken`. Exact declarations are in the
[shipped declarations](../../src/ZkLink/PublicAPI.Shipped.txt) and
[additional declarations](../../src/ZkLink/PublicAPI.Unshipped.txt).

Node CRUD, children/stat and ACL methods are described in the [node reference](nodes.md).
Session ephemerals, descendant counts and identities are described in the
[auxiliary reference](auxiliary-operations.md).

## Configuration

`ConnectAsync` validates options and copies static credentials and certificate
handles synchronously before returning its task. A TLS credentials provider runs
once per endpoint attempt. Endpoint updates apply to future connection attempts.

| Setting | Default and constraints |
| --- | --- |
| `ConnectionString` | Required `host[:port],host[:port][/chroot]`; bracket IPv6; default port 2181, including when TLS is selected |
| `Chroot` | Optional; must agree with an embedded chroot; `/` means no prefix |
| `Tls` | `ZooKeeperTlsOptions.Required()`; secure ports must be explicit |
| `Authentication` | Empty; at most 32 digest entries, each frame-bounded; combined credentials at most 32 MiB |
| `AllowUnsafePlaintextDigest` | False; explicit opt-in and literal loopback addresses required for every plaintext digest endpoint, including endpoint updates; DNS names (including localhost), remote and mixed lists are rejected |
| `EnableTtlNodes` | False; operator assertion that every endpoint enables extended node types; required for TTL creation |
| `CompatibilityProfile` | `ZooKeeper38`; `ZooKeeper39` enables independent watch modes at one path; assert all endpoints qualify, without negotiated discovery |
| `SessionTimeout` | Requests 30 seconds by default. The accepted server timeout governs liveness. |
| `InitialConnectTimeout` | 30 seconds through initial authentication and readiness, across endpoint attempts; completion also waits for owned TLS cleanup |
| `ConnectAttemptTimeout` | 5 seconds for DNS, TCP, TLS and the handshake; reduced by remaining initial/recovery time. Also bounds each platform certificate download where supported |
| `OperationTimeout` | 30 seconds from invocation, including queue time |
| `CloseTimeout` | 5 seconds to obtain acknowledgment before cleanup |
| `MaxInFlightRequests` | 256; range 1–65,536; canceled sent requests retain their slots |
| `MaxQueuedRequests` | 1,024; range 1–1,000,000; full queue fails immediately |
| `MaxPendingRequestBytes` | 32 MiB; range 8 bytes–1 GiB; owned application request payloads, excluding each four-byte frame prefix |
| `MaxFrameBytes` | 1,048,575, excluding four-byte prefix; range 45 bytes–64 MiB |
| `MaxDataBytes` | 1,000,000; range 0–64 MiB, additionally constrained by frame size |
| `MaxCollectionElements` | 100,000; range 1–1,000,000, additionally constrained by frame size |
| `MaxWatches` | 10,000 owned logical handles and bounded cleanup metadata; range 1–1,000,000 |
| `MaxBufferedEvents` | 4,096 by default, range 1–1,000,000. Bounds all buffered watch events after fan-out, each state queue, and the number of active state subscriptions. |

When DNS returns multiple usable addresses, each TCP/TLS attempt gets an equal
share of the remaining endpoint time among the addresses still to try. An early
failure leaves the unused time for later addresses. All TLS attempts validate
the configured server identity.

If an endpoint returns an invalid ZooKeeper handshake, the client closes that
transport and tries another endpoint within the current connection or recovery
deadline. During recovery, each attempt presents the existing session ID. A
response with a different session ID fails handshake validation and allows the
client to try another endpoint within the same recovery deadline. If every
endpoint returns an invalid handshake until the initial deadline expires,
`ConnectAsync` reports `ZooKeeperConnectionFailure.Protocol`. An explicit
session rejection or authentication failure is terminal. Protocol errors during
authentication and watch restoration are also terminal.

The client accepts server-negotiated session timeouts from **100 milliseconds
through 1 hour**, inclusive. A positive timeout outside this range triggers
endpoint failover. A nonpositive timeout signals session rejection on initial
connection or expiry during recovery.

After the handshake is accepted, authentication and watch restoration use the
negotiated inbound liveness timers. Valid replies refresh those timers, so a
progressing restore can exceed `ConnectAttemptTimeout` and the negotiated session
timeout in total. Inbound silence triggers transport recovery at two thirds of
the negotiated timeout and local session loss at the full timeout.
Before the client first becomes ready, the total `InitialConnectTimeout` still
applies through authentication and any retries of the accepted session. Later
reconnects use the negotiated session budget rather than that initial deadline.

Configured timeouts require positive whole milliseconds, at most `int.MaxValue` milliseconds.
Frame/data limits do not change the server's Jute limits. Chroot is namespace
translation; it does not provide ACL isolation or create the chroot node.

## TLS and authentication

Native certificate validation can delay cancellation. The platform may perform
several certificate/revocation downloads, each with its own timeout, before
owned TLS cleanup completes. Consequently initial connection completion can
exceed `InitialConnectTimeout`; these settings are not a hard wall-clock bound
on native validation. macOS ignores the configurable download timeout. See
[online revocation checks and platform limits](../how-to/verify-secure-connections.md#online-revocation-and-connection-cleanup).

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
the dedicated live matrix additionally verifies client TLS/mTLS, certificate
rejection, digest/X.509 ACL distinctions and secure reconnect on both pinned
versions. Other client platforms, deployment revocation services, production PKI rotation and quorum TLS
remain unqualified. See [Verify secure connections](../how-to/verify-secure-connections.md).

`ZooKeeperAuthentication.Digest(user, password)` owns UTF-8 `user:password` bytes,
rejects colons in the username and limits the encoded credential to 1,000,000
bytes. Dispose the configuration object when no longer needed. The client owns
its captured copy until terminal cleanup and clears it then. Multiple auth packets
are acknowledged sequentially and replayed before readiness after reconnect.
Authentication failure is terminal; an operation's `NoAuth` does not by itself
end the session. SASL and read-only mode are unavailable.

Digest credentials establish an identity; a wrong password can establish a
different identity and produce `NoAuth` when accessing a protected node.
Servers enforcing an authentication scheme can reject the first application
operation with `SessionClosedRequiresSaslAuth` and terminal `AuthFailed`, even
after accepting the connection handshake. ZooKeeper also uses that legacy error
code for a missing required digest identity. See the
[required-authentication checks](../how-to/verify-secure-connections.md#required-authentication-policies).

## State, recovery and request outcomes

Snapshots expose `State`, diagnostic `SessionId`, requested/negotiated timeout,
`ConnectionGeneration`, `LastReplyZxid`, `CloseStatus` and optional `ErrorCode`.
They never expose the session password.

| State | Meaning |
| --- | --- |
| `Connecting`, `Authenticating`, `Restoring` | Establishment before application readiness; acknowledged watches are restored before readiness |
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

For standalone writes (`CreateAsync`, `SetDataAsync`, `SetAclAsync`, `DeleteAsync`),
`MultiAsync`, `MultiReadAsync`, `SyncAsync`, `AddWatchAsync`, server-confirmed watch removal,
node reads (`ExistsAsync`, `GetDataAsync`, `GetChildrenAsync`, `GetAclAsync`),
the three one-shot watched reads, and auxiliary queries (`GetEphemeralsAsync`,
`GetAllChildrenNumberAsync`, `WhoAmIAsync`), an invalid body in a complete,
matched success reply fails only that call with `ZooKeeperProtocolException`
and outcome `Unknown`. This includes a create or sync reply that returns a
relative path or a path outside the configured chroot. Queued requests continue
on the same connection unless a batch member has already reported a session or
connection failure. An invalid frame length, an incomplete reply header, or a request
ID out of order faults the session.

Cancellation throws `ZooKeeperOperationCanceledException`, remains ordinary task
cancellation and preserves the outcome. Deadlines throw `ZooKeeperTimeoutException`.
After sent cancellation/timeout the driver retains an ordered entry to drain a
late reply; it still counts toward limits. Positive request IDs wrap without
colliding with pending IDs, and an old connection cannot complete new work.

Each state subscription has one consumer, starts with an atomic snapshot and
then yields ordered transitions. If the next transition would exceed
`MaxBufferedEvents`, the client detaches that subscription and releases its
slot immediately. The detached reader can drain its queued snapshots in order
and then receives `ZooKeeperEventOverflowException`. Other subscriptions and
watches continue running. Total state buffering grows with the number of slow
readers because each active subscription has its own capacity. Subscribe again to get a fresh atomic snapshot.
At the active subscription limit, `SubscribeStateChanges()` throws without
disturbing existing readers. Disposing or cancelling a reader is local.

Restoration failures follow the same session lifecycle. Retryable reset errors
and attempt timeouts keep admission closed while retrying the existing session.
After an established client receives confirmed expiry or authentication failure,
readiness waits and watch streams fail with `ZooKeeperSessionException` and the
terminal state. State and watch enumerators deliver already-buffered items before
raising that failure; acknowledged close completes them normally. Initial connection
failures retain `ZooKeeperConnectionException`. A failed restoration attempt emits
no persistent reconnect-gap event.

## Closing

`CloseAsync` stops admission, ends outstanding caller waits consistently, sends
`closeSession` when possible and awaits acknowledgment within its deadline.
A close acknowledgment requires a matched success reply with an empty body.
Trailing data faults the session and leaves the close unacknowledged.
Success records `Acknowledged`; already confirmed expiry needs no close packet
and records `SessionExpired`. Otherwise close throws `ZooKeeperCloseException`
or cancellation after local cleanup, recording `Unacknowledged`. Repeated calls
return the first close task/result. Terminal failure states remain visible after
disposal.

`DisposeAsync` always releases local resources and does not require acknowledgment.
Unacknowledged close does not prove that the server removed ephemerals.

Acknowledgment does not synchronize every other session's read view. An empty
child-list snapshot also does not guarantee that a subsequent delete will succeed.
The driver preserves a server `NotEmpty` response as `Rejected`, without an
automatic delete retry. See Apache's
[consistency guarantees](https://zookeeper.apache.org/doc/r3.9.6/zookeeperInternals.html).

[Reference](README.md) · [Documentation home](../README.md)

### Rotating TLS credentials

Use `ZooKeeperTlsOptions.RequiredWithProvider` to select a client certificate
and trust roots together on every endpoint attempt. Return a fresh
`ZooKeeperTlsCredentials` built from the current certificates. Its constructor
copies their handles. The driver disposes the returned snapshot after the
attempt, and the application retains ownership of the originals. The provider
receives the attempt cancellation token. At most one provider call is
outstanding per client. Honor cancellation promptly. If a provider never returns,
later attempts on that client cannot reload credentials. After correcting the
provider, create a new client to resume service with a new session.
TLS session resumption is disabled for this mode so
each connection authenticates with the selected credentials.

Connection exceptions expose `TlsFailureReason` and `SocketError` when the
platform provides them. For `TlsFailureReason.ProviderFailed`, inspect
`ProviderFailureReason` to distinguish a callback exception, a null return,
a canceled wait, or a previous invocation still running. Request exception messages include the operation,
outcome and available server code. Paths and endpoints remain available as
explicit properties.
