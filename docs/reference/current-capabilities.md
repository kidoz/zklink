# Current capabilities

This describes the checkout as of **2026-10-27**. No package has been released.

## Code and verification

| Area | Available now | Remaining scope |
| --- | --- | --- |
| Build | .NET 10, C# 14, deterministic builds, locked dependencies, warnings as errors, MIT license | Release package metadata and package qualification |
| Protocol | Internal codecs for all 27 implemented opcodes, handshake/multi/framing, path/chroot translation and Gradle-managed Java fixtures | Remaining live qualification |
| Public types | Immutable snapshots, endpoints, create options, ACLs, versions, request failures, watch handles/events and removal results; contract tests | Package consumer qualification |
| Sessions and operations | Connect/authenticate/sync, snapshots/subscriptions, heartbeat, same-session recovery, cancellation, close, node/ACL methods, typed transactions, auxiliary queries, opt-in TTL creation and watches | Leader/quorum/partition and broader live recovery faults |
| Watches | One-shot reads, persistent/recursive subscriptions, buffered events, shared removal, cancellation cleanup, bounded restoration and explicit reconnect gaps | Broader fault and concurrency qualification |
| Security | TCP/TLS, custom roots/client certificates, live client TLS/mTLS operations, digest replay, certificate rejection and distinct digest/X.509 ACLs | Revocation, rotation, quorum TLS, additional auth schemes and cross-platform qualification |
| Dependency injection | Optional `AddZkLink` registration with one host-owned singleton and asynchronous startup/shutdown | Installed-package and DI-specific Native AOT qualification |
| Analyzers | Six optional ZKL diagnostics, Roslyn compilation tests and a local packed-package consumer check | Released package and cross-platform qualification |
| Local servers | Pinned three-node plaintext/TLS/mTLS fixtures for 3.8.7 and 3.9.6, expired-certificate negative control | Broader fault fixtures |
| Observability | BCL traces and metrics, bounded asynchronous diagnostic delivery, finite tags, listener isolation and redaction checks | Exporter integration, sustained-load and cross-platform qualification |
| Native AOT | Planned requirement | Publish and direct execution evidence for consumers |
| Performance | Initial small GetData codec allocation check | Session/load benchmarks and runtime performance evidence |

See [Tracing and metrics](telemetry.md) for instrument names, timing, privacy and
best-effort delivery semantics.

A dedicated [follower recovery check](../how-to/verify-follower-recovery.md) stops
an observed follower on each pinned version. Scoped evidence covers session and
ephemeral preservation, watch restoration before readiness, explicit request
outcomes and no replay, with verified rollback. Leader/quorum failures and
partitions remain unverified.

## Frameworks and platforms

The current target framework is `net10.0`; the pinned SDK is `10.0.403`. The core
has no third-party runtime dependencies. Public API declarations are checked
against shipped/unshipped baseline files; the shipped baseline is empty.

The [build workflow](../../.github/workflows/build.yml) defines Linux, Windows,
and macOS checks. Local macOS arm64 execution is recorded in the repository;
remote workflow results remain unverified. Proposed Native AOT targets are
`linux-x64`, `linux-arm64`, `win-x64`, and `osx-arm64`; none is qualified yet.

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
these short runs do not qualify sustained load. These are macOS arm64 client observations; remote CI and other
client platforms remain unverified. See [Verify secure connections](../how-to/verify-secure-connections.md). SASL and read-only
mode are outside the planned initial client scope; coordination recipes and
administration require separate designs.

[Reference](README.md) · [Documentation home](../README.md)
