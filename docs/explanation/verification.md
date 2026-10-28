# What the verification layers prove

A passing check answers a particular question. Encoding a record correctly,
starting a ZooKeeper ensemble, and recovering a client session require different
evidence. ZkLink keeps those checks separate so progress in one area does not
imply working behavior elsewhere.

## Server-free tests isolate local contracts

The public-contract suite checks ownership, validation, and failure metadata.
The protocol suite checks serialization, malformed input, frame boundaries,
fragmentation, and stream failures. Both run without a ZooKeeper installation, making local
failures repeatable and independent of container readiness.

Virtual-time peers also check handshake/session identity, negotiated liveness,
cancellation outcomes, ordered late replies, bounded admission and close cleanup.
Loopback TCP/TLS tests exercise sockets and certificate validation with ephemeral
certificates. They do not establish recovery against a real quorum or live ACL
and broad watch semantics. One-shot watch behavior also has separate live evidence.

## Java fixtures provide an independent byte comparison

A serializer and deserializer written together can agree on the same mistake.
The optional Java oracle compares C# primitive and record bytes with Apache's
serializer and asks Java to decode the C# output. The downloaded artifact is
pinned by checksum and source build commit.

The fixture covers primitive boundaries, all implemented request/reply shapes,
handshakes, multi records, and Apache path validation for every UTF-16 code unit.
Java verifies emitted C# requests semantically. This does not establish real
operation commitment or supplementary-character interoperability. The
[oracle inventory](../../tests/ZkLink.JavaOracle/README.md) records its exact scope.

## Fixture health establishes a usable test environment

The Compose health probe uses the Java client supplied with each image to perform
an authenticated create/read/delete with disposable credentials and an ephemeral
node. That is stronger readiness evidence than an open TCP port.

The two-version matrix additionally checks the exact server version on every
node, collects logs, and cleans up isolated projects. This makes it useful for
checking the server environment. A successful
run establishes fixture behavior; driver compatibility still needs ZkLink
requests and assertions against both pinned versions.

`just zk-driver-matrix` runs the managed sample against each node: a new session,
digest authentication, sync and acknowledged close. It has passed on both pinned
versions. `just zk-operations-matrix` also runs the live .NET node/ACL/transaction/auxiliary suite and
CRUD, transaction and auxiliary samples. It checks version conflicts, null/empty/large data, create modes,
chroot, ACL enforcement and acknowledged-close ephemeral deletion. Transaction
cases verify complete rollback and independent read errors. A bounded proxy
consumes/drops a real transaction reply, then allows reconnect so tests can check
actual commitment, session identity and exactly one transmitted write. Auxiliary
cases verify textual-prefix session ephemeral queries, chroot, nested counts,
READ permissions and authenticated/anonymous identities. Broader live
recovery remains unverified beyond the scoped cases below.

One-shot watch tests cover reader ordering, missing-node registration, shared
removal, cancellation/timeouts, global event overflow, unknown lost-response
intents, and restoration of 10,000 long Unicode paths in bounded frames. The
live suite checks events, sharing/removal, chroot, close and same-session recovery
through a full-duplex proxy. A dedicated pause permits mutation and local disposal
while disconnected before restoration is allowed. These observations do not
qualify a general network/quorum fault matrix. Persistent/recursive tests also
check repeated delivery, path boundaries, compatibility-profile restrictions,
independent removal on 3.9 and explicit
reconnect gaps after a missed create/delete interval. Controlled peers cover
gap publication before admission and global overflow, including gap fan-out.

Controlled recovery tests interrupt multi-batch restoration, reject or time out
reset acknowledgments, inject stale generation callbacks, and distinguish
terminal expiry/authentication from local loss. They verify request outcomes,
stream completion, and that a consumed one-shot is not restored again. The live
proxy can hold and drop one server-applied reset acknowledgment before pausing
reconnect. Tests verify closed admission, same-session retry, one gap after
successful restoration, and no revival of a disposed handle. A short negotiated
session timeout separately verifies local `Lost` while reconnect stays paused.
These checks run on both pinned versions; live forced server expiry and the
broader quorum/soak campaign remain open.

`just zk-ttl-matrix` separately starts TTL-disabled and TTL-enabled fixtures for
each patch. It checks opt-in, maximum TTL, single/atomic creation, close
survival, child protection and eventual cleanup, plus disabled-server errors and
rollback. Bounded cleanup polling establishes eventual removal under the test
conditions, without promising exact deletion timing.

## Secure server checks keep authentication and authorization distinct

`just zk-security-matrix` uses separate three-node client TLS/mTLS ensembles on
both pinned versions. Secure authenticated readiness precedes .NET tests.
The .NET client validates custom trust and host identity, exercises node and
transaction operations, restores one-shot/persistent watches through an opaque
TLS tunnel, and closes with acknowledgment. Digest-only ACLs prevent a shared
client certificate from bypassing digest authorization checks. Different trusted
client certificates also demonstrate distinct X.509 ACL identities.

Negative controls reject wrong hostnames, unknown roots, expired server
certificates and TLS-required connections to a plaintext port. mTLS also rejects
configured missing/untrusted/expired client certificates. A valid control remains
usable, so connection rejection is distinguishable from server unavailability.
Depending on TLS alert timing, an mTLS rejection can surface during TLS or during
the subsequent connection attempt; no ready ZooKeeper client is returned.

`just zk-security-concurrency` separately releases seven rejection attempts
together after all actors establish valid controls. Three rounds per patch retain
the original four-second initial connection budget while another authenticated
client repeatedly makes progress. Timing labels distinguish certificate loading,
option capture, connection completion and disposal. Earlier intermittent timeouts
remain unexplained; passing bounded rounds does not establish a root-cause fix or
qualify sustained failure traffic. CI includes the command; remote results have
not yet been observed.

The runner removes its generated private stores separately from retained logs.
macOS uses temporary platform key storage for imported test certificates; every
handle is disposed. Revocation is disabled only for the private test CA, which
has no revocation service. These results establish client TLS behavior on the
observed macOS arm64 platform, without qualifying revocation, rotation, quorum
TLS, SASL, Native AOT or other client operating systems. See
[Verify secure connections](../how-to/verify-secure-connections.md).

## Telemetry listeners verify observable behavior

Controlled listeners check caller outcomes, monotonic durations, parent correlation,
redaction, retained tombstones and gauge cleanup. A blocked listener fills the
bounded diagnostic queue while actual driver operations and acknowledged close
continue; throwing activity and histogram callbacks are isolated and counted.
Live checks on both pinned versions observe CRUD, ACL rejection and same-session
watch restoration. Secure rejection cases also observe TLS failure activities.
See [Tracing and metrics](../reference/telemetry.md) for delivery limits and names.
These checks do not provide benchmark or sustained-load evidence.

## Real follower outages verify endpoint recovery

`just zk-follower-faults` runs separately from ordinary operation and security
checks. It stops one observed follower while two voting members remain, then
checks session/ephemeral preservation, authentication and watch restoration,
explicit gap/readiness ordering and an uncertain sequential create without replay.
Forwarding endpoints hold selected replies to make those observations repeatable;
the server stop causes the transport loss. Both pinned versions have scoped local
evidence, with server restoration and owned-volume cleanup. A deliberate abort
also exercised rollback. See [Verify follower recovery](../how-to/verify-follower-recovery.md).
`just zk-leader-faults` shares the outage scenario and additionally requires a
replacement leader among the two survivors, followed by the old leader joining as
a follower while that replacement stays leader. Election-time connection failures
reach the driver through proxies that keep accepting later attempts. See
[Verify leader recovery](../how-to/verify-leader-recovery.md).
`just zk-quorum-faults` stops two voters and checks brief valid-session recovery
and longer-outage local `Lost` on both versions. The tested client is never
replaced and uncertain creates are never replayed. Independent control sessions
observe results after all members recover; local loss does not establish
server-confirmed expiry. See [Verify quorum recovery](../how-to/verify-quorum-recovery.md).
`just zk-partition-faults` additionally discards either direction of client
traffic independently. Incoming-loss cases continue forwarding outgoing
heartbeats. Outgoing-loss cases discard writes and heartbeats while proving
incoming watch delivery still works; discarded writes must remain absent from
the server after healing. The caller still receives `Unknown`, because it lacks
the proxy's knowledge. Short healing must restore a valid session and
watches; longer silence must produce terminal local `Lost`, with no replay or
owner replacement after healing. A separate probe verifies authentication and
acknowledged close through the healed proxy. This is application-frame loss with
live TCP control traffic. See [Verify one-way traffic recovery](../how-to/verify-partition-recovery.md).
`just zk-peer-partition-faults` detaches an observed leader or follower's peer
interface in separate fixtures on each pinned version. Bidirectional peer TCP
probes must fail while the majority commits a protected write. Follower cases
retain the existing leader; leader cases require replacement election before
majority progress. The selected process and client network remain intact; ZooKeeper may
close its client listener after detecting quorum loss. An independent minority
write cannot acknowledge success, and the owner must recover its session,
ephemeral and watches through the majority without replaying an uncertain create.
Healing verifies original process/client identities, full readiness and consistent
majority data through every member. An isolated old leader is reattached only
after the replacement majority is serving and must return as a follower. See
[Verify peer partition recovery](../how-to/verify-peer-partition-recovery.md).
Asymmetric peer traffic loss, simultaneous network faults and sustained load remain separate work.

`just zk-watch-pressure-faults` holds events unread while observing real
heartbeats, acknowledged pings and ordinary reads. Two shared persistent handles
and one recursive handle exercise logical fan-out against the global event budget.
A separate case fills that budget with state and watch entries, then requires an
explicit terminal overflow after retaining the earlier buffered entries in order.
Repeated rounds check full handle-capacity reuse, unread-handle disposal and
quiescent resource counters. See [Verify watch pressure](../how-to/verify-watch-pressure.md).
These checks provide bounded ownership evidence; long-running resource/load
qualification remains open.

`just zk-soak` repeatedly exercises bounded concurrent CRUD, client creation and
disposal, cancellation before and after transmission, and same-session watch
restoration with a held acknowledgment. Quiescent checkpoints require zero owned
watch/event/request state and no remaining owned proxy listeners or live sockets.
The harness streams resource samples and seed/outcome totals, with explicit
retained-heap and thread-growth bounds after initial rounds. See
[Run a seeded soak](../how-to/run-seeded-soak.md) for duration, scope and thresholds.

The server-free [benchmark suite](../how-to/run-benchmarks.md) measures request
encoding, owned reply decoding and synchronous in-memory framing. Its allocation
gate uses the same workloads, with reviewed per-case byte limits and retained
numeric observations. BenchmarkDotNet also records raw timing/allocation/GC data
and machine metadata. These measurements do not qualify live driver throughput,
request latency, concurrent load or recovery performance. CI gates allocations;
shared-runner timings remain informational.

The separate [live request benchmark runner](../how-to/run-live-benchmarks.md)
uses one measured session at 1/32/256 concurrency against each pinned server
version. It validates every request, retains complete bounded latency histograms
and observes process allocations, collections and sampled memory. CI checks the
bounded workload and owned cleanup; timing and resource values remain
informational local observations. Watch/recovery and TLS performance need their
own measurements.

The [shared watch fan-out runner](../how-to/run-watch-benchmarks.md) measures
mutation-to-consumer delivery for 1/32/256 persistent handles. Serialized mutations,
per-consumer counts, protected progress reads and queue-counter boundaries establish
the measured scope. Complete latency histograms and process resources are retained;
stream termination, independent namespace cleanup and failed-process fixture
cleanup are checked. This is a bounded delivery workload; distinct-path recovery is measured separately
and broader performance qualification remains open.

The [distinct-path recovery benchmark](../how-to/run-watch-recovery-benchmarks.md)
registers 10,000 persistent watches, verifies native bounded multi-frame restoration
through an owned inspecting proxy, and checks session/ephemeral preservation,
readiness gating, every reconnect gap and subsequent invalidation/protected read.
Timings separate intentional gates from active recovery observations. The smaller
CI workload checks the same invariants at 100 paths; full local measurements and
process resources are retained separately from broader release qualification.

## Release claims need consumer evidence

The build workflow defines checks on Linux, Windows, and macOS. A workflow file
records the intended checks; observed runs provide the results. Native AOT needs
another layer: publishing consumers for each declared platform and directly
executing the native output. Package examples must also compile against the
packed release candidate.

Those release checks remain open. The
[capability reference](../reference/current-capabilities.md) separates current
code, recorded checks, and unverified targets. Use
[Run the development checks](../how-to/run-development-checks.md) for executable
commands and [Run a local ZooKeeper ensemble](../how-to/run-local-zookeeper.md)
for fixture operations.

[Explanation](README.md) · [Documentation home](../README.md)
