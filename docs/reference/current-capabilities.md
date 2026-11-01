# Current capabilities

This describes the checkout as of **2026-11-01**. The published core package is
**0.1.0**; **1.0.0** is an unpublished candidate.
See [Production release readiness](release-readiness.md) for the current core
release scope, blocking evidence and delivery order.

## Code and verification

| Area | Available now | Remaining scope |
| --- | --- | --- |
| Build | .NET 10, C# 14, deterministic builds, locked dependencies, warnings as errors, MIT license | Observed cross-platform and release qualification |
| Core packaging | Version 0.1.0 release workflow, metadata/documentation/notices, portable symbols, Source Link and embedded-source checks; Docker-free inspection and installed-consumer identity checks passed on Linux, Windows and macOS; Linux live examples on both server versions | Released-version compatibility validation, managed TLS package-consumer coverage and full release qualification |
| Protocol | Internal codecs for all 27 implemented opcodes, handshake/multi/framing, path/chroot translation and Gradle-managed Java fixtures | Remaining live qualification |
| Public types | Immutable snapshots, endpoints, create options, ACLs, versions, request failures, watch handles/events and removal results; contract tests and local core package consumer | Compatibility validation against future released versions |
| Sessions and operations | Connect/authenticate/sync, snapshots/subscriptions, heartbeat, same-session recovery, cancellation, close, node/ACL methods, typed transactions, auxiliary queries, opt-in TTL creation and watches; observer writes and recovery on both pinned versions | Final-candidate sustained qualification |
| Watches | One-shot reads, persistent/recursive subscriptions, buffered events, shared removal, cancellation cleanup, bounded restoration and explicit reconnect gaps | Broader fault and concurrency qualification |
| Security | TCP/TLS, custom roots/client certificates, live TLS/mTLS/digest/ACL checks; managed and native online-revocation controls on four client targets and bounded platform certificate downloads | Deployment-specific revocation, rotation and final-candidate qualification; quorum TLS and additional auth are separately scoped |
| Dependency injection | Optional `AddZkLink` registration with one host-owned singleton and asynchronous startup/shutdown | Installed-package and DI-specific Native AOT qualification |
| Analyzers | Six optional ZKL diagnostics, Roslyn compilation tests and a local packed-package consumer check | Released package and cross-platform qualification |
| Local servers | Pinned three-node plaintext/TLS/mTLS fixtures for 3.8.7 and 3.9.6, expired-certificate negative control | Broader fault fixtures |
| Observability | BCL traces and metrics, bounded asynchronous diagnostic delivery, finite tags, listener isolation and redaction checks | Exporter integration, sustained-load and cross-platform qualification |
| Native AOT | Rooted/unrooted packed consumers, direct native execution and twelve TLS/mTLS live cases per target on Linux x64/arm64, Windows x64 and macOS arm64, including sent-cancellation/`Unknown`; five online-revocation controls per target | Full release qualification; AOT metadata is enabled only in the unpublished 1.0.0 candidate |
| Performance | Protocol/framing benchmarks and allocation gates; live get/set concurrency, shared persistent-watch fan-out and 10,000-path recovery measurements with latency/resource observations | Repeated controlled-runner measurements, TLS performance and broader qualification |

See [Tracing and metrics](telemetry.md) for instrument names, timing, privacy and
best-effort delivery semantics.

See [Verify a local core package](../how-to/verify-core-package.md) for archive,
source/symbol and installed-consumer checks. These use local plaintext fixtures;
the release workflow verifies the exact package before publishing. Native AOT support remains separately qualified.

A dedicated [follower recovery check](../how-to/verify-follower-recovery.md) stops
an observed follower on each pinned version. Scoped evidence covers session and
ephemeral preservation, watch restoration before readiness, explicit request
outcomes and no replay, with verified rollback. A separate
[leader recovery check](../how-to/verify-leader-recovery.md) also verifies replacement
election and old-leader return as a follower.
[Required authentication checks](../how-to/verify-secure-connections.md#required-authentication-policies)
cover two UTF-8 digest identities, protected watch restoration after auth replay,
per-operation `NoAuth` and terminal rejection of unauthenticated or unsupported
SASL clients on both pinned versions. SASL remains unsupported.
[Server-expiry checks](../how-to/verify-session-expiry.md) verify terminal
`Expired`, watch/readiness termination, ephemeral removal and no replacement
session or committed-create replay on both pinned versions. These managed
checks use an exact server management operation in separate owned fixtures.
[Quorum recovery checks](../how-to/verify-quorum-recovery.md) cover a brief
two-voter outage with session/watch recovery and a longer interruption ending
in local `Lost`, without automatic replacement or uncertain-create replay.
[One-way client-traffic checks](../how-to/verify-partition-recovery.md) cover both
directions independently, with valid-session recovery or terminal local loss.
Outgoing cases verify incoming watch delivery and server absence of discarded
writes without delayed forwarding or replay.
[Peer partition checks](../how-to/verify-peer-partition-recovery.md) detach only an
observed leader or follower's peer interface in separate cases on both patches,
preserving its process and client network. Leader cases require a replacement
election and old-leader return as a follower; follower cases retain the existing
leader. Both verify majority writes, minority write failure, retained session/watch
recovery, no replay and replica consistency after healing. Asymmetric peer traffic
loss, simultaneous network faults and broader qualification remain unverified.

[Watch-pressure checks](../how-to/verify-watch-pressure.md) cover unread persistent
and recursive fan-out with live heartbeats/requests, explicit shared state/watch
buffer overflow, retained event order, unread-handle disposal and repeated
handle/counter reuse on both patches. Longer-running resource and load
qualification remains open.

[Seeded soak workloads](../how-to/run-seeded-soak.md) combine bounded concurrent
CRUD, repeated client lifecycles, cancellation outcomes and same-session watch
recovery. Resource checkpoints check owned counters/connections and compare
retained managed heap and threads against a recorded baseline. The harness
supports configurable durations. `just zk-soak-isolated` adds a pinned Linux
runtime, private proxy listeners, source/binary integrity checks and bounded
supervision; native/isolated short workloads and owned driver-exit/interruption
cleanup pass on both patches locally and in Linux CI. A passing short run does
not establish the six-hour-per-version release minimum or cross-platform
qualification. The release-duration
attempt on 29 October stopped after about two hours on 3.8.7 with a `DeleteAsync`
timeout; fixture rollback completed, and the cause remains under investigation.
Bounded follow-up workloads and concurrent scratch-file deletion did not
reproduce it. Controlled late-sync checks now verify `Unknown`, ordered reply
drain, the same session and no replay on both patches. The longer diagnostic replay stopped after about
45 minutes on 3.8.7 with a wait timeout followed by a proxy framing error during
cleanup. Both errors were retained; fixture rollback completed. Journals now
label guarded waits and preserve safe rejected-frame metadata at its original
observation time, with server-free regression checks and fresh five-minute
workloads passing on both versions. Controlled-peer delete checks preserve
operation metadata, late-reply draining and no replay after a deadline. Proxy
checks cover truncated prefixes and bodies in both directions, followed by a
clean reconnect. Both historical causes remain open.

## Frameworks and platforms

The current target framework is `net10.0`; the pinned SDK is `10.0.403`. The core
has no third-party runtime dependencies. Public API declarations are checked
against shipped/unshipped baseline files; the core's shipped baseline records
the initial 0.1.0 API. Package inspection also compares the packed API with the
published 0.1.0 package using SDK rules, including parameter names and attributes,
and verifies rejection of altered incompatible copies. The 401 declared entries
match the released source baseline; behavioral and final-candidate review remain
separate requirements.

The [build workflow](../../.github/workflows/build.yml) defines Linux, Windows,
and macOS checks. The separate [native verification harness](../how-to/verify-native-aot.md#evidence-and-remaining-qualification)
passed all eight jobs on 2026-10-29 for `linux-x64`, `linux-arm64`, `win-x64`
and `osx-arm64`: packed rooted/unrooted publication, direct native identity and
twelve secure live cases per target. Linux uses Docker fixtures; Windows/macOS
use portable JVM fixtures. Retained artifacts cover 48 live cases and 64 final
authenticated readiness probes across both server versions. Full release
qualification remains open. The unpublished 1.0.0 candidate enables
`IsAotCompatible`; it is subject to repeat verification after that metadata
change. A [later shared-candidate run](https://github.com/kidoz/zklink/actions/runs/41239985235)
also passes native sent-cancellation/`Unknown` and five dedicated online-revocation
controls on each target, using the same package bytes as the managed consumers.

## Server targets

| ZooKeeper version | Intended role | Fixture settings | Driver compatibility |
| --- | --- | --- | --- |
| 3.8.7 | Required qualification target | [Version settings](../../dev/zookeeper-3.8.7.env) | Connection, node/ACL, transaction, auxiliary, TTL and watch tests passed |
| 3.9.6 | Required qualification target and development default | [Version settings](../../dev/zookeeper-3.9.6.env) | Connection, node/ACL, transaction, auxiliary, TTL and watch tests passed |

The [project overview](../../README.md#local-zookeeper-testing) records fixture
smoke checks on 2026-10-25 for both versions: three-node startup, exact-version
verification, authenticated create/read/delete, and cleanup. Those readiness checks use
the Java client in each image. `just zk-driver-matrix` additionally passed new
session, digest auth, sync and acknowledged close through ZkLink on every node.
`just zk-operations-matrix` adds live .NET CRUD/stat/children, version conflicts,
null/empty/large data, create modes, chroot, ACL checks and acknowledged-close
ephemeral deletion, plus the CRUD example on each node. This remains scoped
operation evidence rather than full driver qualification. Transaction tests also
verify rollback, member-specific read failures and a real committed reply dropped
before same-session resume, without request replay. Auxiliary cases verify session-only
ephemerals, textual prefixes, chroot, nested counts, permissions and identities;
see the [auxiliary reference](auxiliary-operations.md). `just zk-ttl-matrix`
separately verifies TTL-enabled and TTL-disabled servers on each patch, including
client opt-in, both create modes, chroot, maximum TTL, close survival, child
protection and eventual cleanup. See [TTL node creation](ttl-nodes.md).

One-shot watches have controlled-peer race/overflow and 10,000-path restoration
evidence. Live tests cover event types, sharing, removal, chroot, close and a real
connection cut followed by same-session restoration. Persistent/recursive checks
add repeated delivery, chroot and explicit reconnect gaps. The default 3.8
profile rejects conflicting modes at one path; the 3.9 profile additionally
verifies independent categories at the same path. The `--watches` sample runs
on every node. Recovery checks also cover interrupted/reset-timeout retries,
stale generations, terminal expiry/authentication, and consumed one-shots in
controlled peers. Both live versions additionally exercise dropped reset
acknowledgments and local loss while reconnect is paused. See the
[watch reference](watches.md).

TLS is required by default. Loopback tests cover trusted/custom roots, wrong
identity, unknown issuer, expiry, mutual authentication, missing required client
certificate and plaintext rejection on local macOS arm64. The default local fixtures use plaintext; `just zk-security-matrix` separately
passed 9 TLS and 13 mTLS cases per pinned patch, including secure operations,
watch recovery, server/client certificate rejection and X.509 ACL identity
isolation. `just zk-security-concurrency` adds three bounded rounds of seven
synchronized rejection attempts per patch, with repeated valid-control requests.
The earlier intermittent concurrency timeout has not been reproduced or explained;
these short runs do not qualify sustained load. Linux CI has also passed the
secure fixture and bounded-concurrency jobs. The separate native checks cover
TLS/mTLS on all four client targets; these live fixtures use `NoCheck` and do
not establish production online-revocation behavior. Dedicated managed and native
loopback responders additionally verify valid, revoked, unavailable and invalidly
signed status and held-response cancellation on all four hosted targets. On the
local macOS 27 host, an active VPN, proxy or network filter accompanies failed
loopback retrieval; causality has not been established. See
[Verify secure connections](../how-to/verify-secure-connections.md). SASL and read-only
mode are outside the planned initial client scope; coordination recipes and
administration require separate designs.

[Reference](README.md) · [Documentation home](../README.md)
