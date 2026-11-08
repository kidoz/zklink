# Changelog

All notable changes to this project will be documented in this file.

ZkLink is in initial development. The initial core release was **0.1.0**.
Release verification covers the documented scenarios and does not establish
broader security, Native AOT or production qualification. See
[README.md](README.md) for the current status and verified scope.

## [Unreleased]

### Fixed

- Restrict unsafe plaintext digest authentication in the core to literal loopback
  endpoints, rejecting DNS names (including localhost), remote and mixed lists.
- Align recursive-watch matching with the existing routing rule excluding
  child-list notifications.
- Consolidate current capability and Native AOT claims and preserve superseded
  release evidence in historical records.

## [1.0.3] - 2026-11-07

### Fixed

- Synchronize election cleanup reads after other participants close, preventing
  stale candidate listings from being mistaken for leftover ephemeral nodes.

### Added

- Verify packed `ZkLink` and `ZkLink.Extensions.DependencyInjection` hosts under
  Native AOT on Linux x64/arm64, Windows x64 and macOS arm64. Rooted analysis and
  an unrooted DI host run 48 secure lifecycle cases across ZooKeeper 3.8.7 and 3.9.6.

### Changed

- Enable trim and Native AOT analyzers for the dependency-injection package.
- Require a scheduler owner when constructing pending requests, simplifying
  cancellation and timeout callback state.

## [1.0.2] - 2026-11-07

### Fixed

- Preserve the watch-restoration watermark across interrupted attempts, committing
  reply progress only after every reset batch succeeds. One-shot data and child
  invalidations remain eligible for delivery when later batches must be retried.
- Include authentication and accepted-session retries in the initial connection
  deadline, and preserve TLS failure classification across later socket failures.
- Withdraw canceled and expired queued requests before transmission, and measure
  recovery deadlines from the oldest unanswered application or maintenance request.
- Finalize session failures before stopping the transport and recheck inbound
  activity before committing a heartbeat timeout.
- Match one-shot watch notifications by registration mode and whether the watched
  node existed. Removal notifications remain specific to the watch category.
- Restrict the basic sample's plaintext credential override to loopback endpoints.

### Changed

- Reuse the heartbeat timer and dispatch watch events without intermediate target
  lists to reduce per-activity allocations.

## [1.0.1] - 2026-11-07

### Added

- Publish `ZkLink`, `ZkLink.Extensions.DependencyInjection` and `ZkLink.Analyzers`
  together at the same version, with installed-package checks for all three.
- Include repository metadata, license and third-party notices in the analyzer
  package. Core and DI include adjacent portable symbol packages.

### Fixed

- Serialize fault-proxy tests that scan host processes and align their signal
  observation guard with the existing bounded attribution lookup.
- Use the simpler verified-package and NuGet trusted-publishing release flow.
  Sustained soak and controlled benchmarks remain separate production
  qualification and are not claimed by this release.

## [1.0.0] - 2026-11-07

This release preserves the existing 0.1.0 shipped API baseline; new array
overloads are recorded separately and the dependency-injection API is revised.
Sustained resource and performance qualification remains in progress; see
[release readiness](docs/reference/release-readiness.md) for the open evidence.

### Added

- Null-preserving `byte[]?` overloads for node create/update, transaction create/update
  descriptors and `ZNodeData` construction, alongside the existing memory overloads.
- A constructor-injectable `IZooKeeperClientAccessor` for host-owned sessions. This
  replaces direct client registration in the unpublished DI integration and avoids
  per-resolution disposal tracking and child-scope disposal failures.
- NuGet package metadata and portable symbols for the DI project. Publication and
  DI-specific Native AOT qualification remain separate checks.
- A leader election tutorial and runnable sample using three separate sessions,
  ephemeral sequential candidates and predecessor watches, verified in the
  operations matrix on both pinned server versions.

- Soak socket ownership checks now retain original handles and exact connection
  endpoints, excluding unrelated namespace listeners that share a port number.
  Bounded failure snapshots and regression controls preserve detection of
  retained listeners and TCP peers under the existing deadlines.

- `just zk-soak-isolated [seconds] [seed]` runs the soak in a pinned Linux
  runtime with private proxy listeners, source/binary integrity checks, bounded
  supervision and retained resource/cleanup evidence. CI checks short workloads,
  driver termination and supervisor interruption on both server versions.

- Observer-endpoint verification on both pinned server versions, including
  writes, same-session watch recovery and authenticated cleanup.
- Native sent-cancellation verification with an independently observed committed
  write, `Unknown` outcome and no replay; dedicated native online-revocation
  controls are included in candidate verification.
- One shared candidate bundle for managed/native verification, with immutable
  package/symbol checksums, provenance and required production-evidence checks
  before publication. Public-feed installation and indexed symbols are checked
  after publishing.

- Docker-free core package inspection and installed-consumer identity checks,
  with separate Linux, Windows and macOS CI jobs. `just package-inspect` runs
  these checks locally; the Linux live consumer matrix remains separate.
- Windows/macOS native live CI uses disposable three-voter Java ensembles for
  both pinned ZooKeeper versions, with Gradle-verified dependencies, portable
  certificate generation, authenticated readiness and owned process cleanup.
  `just aot-portable-check` runs the same native checks without Docker or the
  OpenSSL CLI; remote platform qualification remains separately recorded.
- Recorded successful native publication and direct execution on all four
  targets, plus 48 secure live cases across both pinned server versions.

### Fixed

- Bound certificate status downloads by the connection-attempt budget where the
  platform supports per-request limits, and recheck cancellation before exposing
  an authenticated transport. macOS native certificate cleanup can outlive the
  caller's cancellation or deadline; see
  [secure connections](docs/how-to/verify-secure-connections.md) for the platform limits.

- Native publication uses the tracked NuGet configuration filename casing on
  case-sensitive filesystems.
- Windows security fixtures import client certificates with temporary platform
  key storage required by Schannel, releasing owned certificates on disposal.

## [0.1.0] - 2026-10-28

### Fixed

- Requests remain owned by the scheduler when xid allocation fails, so teardown
  completes their tasks and releases their byte reservations.
- Automatic watch cleanup retains retry intent after a local preparation failure
  on a live connection and observes every background task exception.
- Watch documentation consistently states that canceling event enumeration
  disposes the registration locally and schedules best-effort server cleanup.
- Watch restoration now retries timeout/session-moved responses within the owned
  session budget and treats expiry/authentication responses as terminal failures.
- Established-session readiness waits and watch streams report terminal session
  failures consistently during reconnect.
- Restoration completion, readiness and gap publication share the session lock,
  preserving cleanup when disposal races the final acknowledgment.
- Close now rejects unsent watch maintenance instead of allowing it onto the wire
  after `closeSession`.
- Meter construction now uses the same listener-failure isolation as instrument
  registration.
- Protocol writer size calculations can no longer surface checked-integer
  overflow instead of the documented malformed-record failure.
- Local watch cleanup now checks capacity explicitly before recreating bucket
  metadata.

### Changed

- Updated the pinned .NET SDK to 10.0.403, retaining exact-version selection.
- Removed telemetry gauge observation from scheduler paths that cannot change
  request, byte, or tombstone pressure.
- Added debug-only assertions for caller-held session-gate contracts in request,
  watch, and shared event-budget bookkeeping.
- Extracted per-attempt handshake, authentication, and watch-restoration
  sequencing from the session orchestrator without changing session ownership.
- Narrowed the watch manager's session dependency to an explicit internal
  handoff with direct substitute-based coverage.
- Clarified and directly tested terminal state-stream completion after buffered
  snapshots, matching existing watch-stream behavior.

### Added

- A core NuGet release workflow with tag/version/changelog validation, reusable
  build/live/native verification gates, installed-consumer checks of the exact
  release package, provenance attestations and NuGet trusted publishing.

- Native AOT analysis and packed-package smoke consumers, host-specific publish
  commands, executable identity checks and isolated TLS/mTLS live runners.
  Separate CI jobs define four-RID publication and Linux live execution.

- `just package-check` builds and inspects a local core candidate and portable
  symbols, checks embedded sources and dependencies, then compiles and runs
  installed-package examples on every node of both pinned server versions.
  A separate CI job retains the candidate and verification evidence.

- `just bench-recovery [watches]` measures controlled same-session recovery of
  10,000 distinct persistent-watch paths on both pinned versions, with native
  restore-frame/path counts, held-ack readiness checks, complete gap delivery,
  all-path post-recovery validation, resource reports and a smaller CI selection.

- `just bench-watches [seconds]` measures persistent-watch fan-out to 1, 32 and
  256 logical consumers on both pinned versions, with exact delivery/progress
  checks, bounded queues, latency/resource reports, shared live-fixture tooling
  and a separate CI smoke selection.

- `just bench-live [seconds]` measures get/set workloads at 1, 32 and 256
  concurrent requests on both pinned server versions, with latency histograms,
  process allocation/GC/memory observations, isolated case processes and a
  bounded CI smoke selection with verified fixture cleanup.

- A server-free BenchmarkDotNet suite for protocol encoding/decoding and framing,
  with raw results, environment metadata, reviewed allocation budgets and a
  separate allocation CI job. Run `just bench` or `just bench-allocations`.

- `just zk-soak [seconds] [seed]` runs a bounded seeded workload on both pinned
  versions, combining concurrent CRUD, client churn, cancellation and same-session
  watch recovery with streaming resource evidence, growth thresholds and a
  separate short CI selection. Durations support two-minute defaults and longer
  explicit runs with retained seed and outcome counts.

- `just zk-watch-pressure-faults` verifies slow watch consumers, actual heartbeat
  progress, shared state/watch event limits, explicit overflow with buffered FIFO
  delivery, unread-handle disposal and repeated capacity/counter cleanup on both
  pinned versions, with a separate CI selection and failed-run cleanup checks.

- Leader peer-isolation cases extend `just zk-peer-partition-faults` to separate
  leader and follower cases on both pinned versions, verifying replacement
  election, majority progress, session/watch recovery, no replay and old-leader
  return as a follower, with role-specific artifacts and guarded healing.

- `just zk-peer-partition-faults` isolates an observed follower's peer interface
  on both pinned versions, preserving process/client identities and verifying
  majority progress, minority write failure, same-session/watch recovery,
  no replay and replica consistency after healing, with a separate CI selection.
- Outgoing request/heartbeat loss cases extend `just zk-partition-faults` to both
  directions independently, checking incoming watch delivery, accurate unknown
  outcomes and no forwarding or replay of discarded writes after healing.

- `just zk-partition-faults` checks one-way incoming traffic loss, forwarded
  heartbeats, session/watch recovery or terminal local loss and healed-proxy
  cleanup on both pinned server versions.

- `just zk-quorum-faults` checks brief recovery and terminal local loss after
  stopping two voters, with no uncertain-create replay and verified cleanup on
  both pinned server versions.

- `just zk-leader-faults` verifies replacement election, preserved sessions and
  watches, no uncertain-create replay and old-leader return as a follower on both
  pinned server versions, using shared follower/leader fault infrastructure.

- Dedicated follower-outage tests, labeled disposable ensembles and
  `just zk-follower-faults` with a separate CI job on both pinned server versions.
- Verification of retained sessions/ephemerals, watch restoration before readiness,
  uncertain sequential creates without replay, and bounded rollback after a stop.

- BCL tracing and metrics for connections, operations, recovery, heartbeat and
  watch pressure, with bounded diagnostic delivery and sensitive-data exclusion.
- Controlled listener tests and live telemetry verification on both pinned
  ZooKeeper versions, including secure connection rejection.

- Disposable client TLS/mTLS ensembles, generated certificates, certificate
  rejection and secure node/transaction/watch recovery tests on both pinned patches.
- Distinct digest and X.509 ACL checks, trusted connections on every secure node,
  and explicit plaintext/expired-certificate negative controls.
- `just zk-security-matrix`, a dedicated CI job, private fixture cleanup and
  scanning of retained evidence for private material.
- `just zk-security-concurrency` runs synchronized TLS rejection rounds with
  valid control requests and safe timing diagnostics on both pinned patches.

- Optional `ZkLink.Analyzers` Roslyn package with six security, watch-ownership
  and async-usage diagnostics, compilation tests and packed-consumer verification.

- Optional `ZkLink.Extensions.DependencyInjection` registration with one shared
  host-connected client, asynchronous startup, acknowledged shutdown and lifecycle tests.

- Controlled tests for interrupted multi-batch restoration, stale generations,
  consumed one-shots and terminal session outcomes, plus live dropped-reset-reply
  and local-loss checks on both pinned server versions.

- Persistent and recursive `AddWatchAsync` subscriptions with repeated buffered
  delivery, profile-aware shared removal and bounded extended restoration.
- Explicit `ReconnectedWithGap` events with connection generations, controlled
  cancellation/overflow checks and live reconnect tests on both pinned patches.
- Safe common-profile watch ownership for 3.8 servers, with independent modes
  at one path enabled by the existing 3.9 compatibility profile.
- Correct successful add-watch decoding and a Java-generated acknowledgment fixture.

- One-shot existence/data/children watched reads with owned handles, immutable
  events, single-consumer streams and reader-ordered registration.
- Shared/local/server-confirmed removal, canceled-request cleanup, a shared
  state/watch event budget and same-session restoration before readiness.
- Controlled-peer watch races and 10,000-path restoration checks, live watch tests
  on both pinned patches, and a runnable `--watches` example.

- Explicit `EnableTtlNodes` opt-in for TTL creation in `CreateAsync` and
  `MultiAsync`, retaining integer-millisecond bounds and no automatic replay.
- TTL-enabled/disabled fixtures, a separate live CI job and `just zk-ttl-matrix`
  for both pinned versions, including eventual cleanup and container disappearance.

- Public `GetEphemeralsAsync`, `GetAllChildrenNumberAsync` and `WhoAmIAsync`, with
  immutable results, textual-prefix/chroot semantics and existing request outcomes.
- Controlled-peer and two-version live auxiliary tests, plus a runnable
  `--auxiliary` sample that prints no identity values.

- Root MIT `LICENSE` and central package author, copyright and license metadata.

- Typed `MultiAsync` atomic writes and `MultiReadAsync` read batches, with closed
  operation/result types, ordered owned results and whole-batch input capture.
- Complete rejected-transaction diagnostics with the actual request ID, failing
  member index and raw member codes; unknown outcomes are never replayed.
- Controlled-peer cancellation/validation checks and live transaction success,
  rollback, chroot, read-error and real dropped-response tests on both patches.
- A runnable `--transactions` example and transaction guides; the live matrix
  exercises it on every node.

- Public node operations: exists, data, children, create, set-data and delete,
  plus ACL reads/replacement with separate data and ACL version conditions.
- Persistent, ephemeral, sequential and container creation, with explicit ACLs,
  returned path/stat and chroot translation; TTL creation requires explicit opt-in.
- Live xUnit node/ACL tests on ZooKeeper 3.8.7 and 3.9.6, including null/empty and
  large data, conflicts, authorization and ephemeral removal after close.
- `just zk-operations-matrix`, a separate live CI job with retained logs, and a
  runnable CRUD example through the basic sample's `--nodes` option.

- Connectable client with validated options, TCP/TLS, optional custom trust roots
  and client certificates, digest authentication and same-session reconnect.
- Immutable session snapshots and atomic state subscriptions with bounded buffering.
- Request scheduling with count/byte limits, ordered replies, cancellation and
  timeout outcomes, late-reply bookkeeping and no automatic application replay.
- Negotiated-timeout heartbeats, endpoint updates and acknowledged asynchronous
  close with cleanup on cancellation or failure.
- Public `SyncAsync`, a runnable connection sample and managed connection smoke
  checks against every node of ZooKeeper 3.8.7 and 3.9.6.
- Virtual-time lifecycle tests and loopback TLS/mTLS certificate checks. Live
  secure-server and broader recovery qualification remain open.

- Initial .NET 10 solution (`src/ZkLink`) with C# 14, nullable reference types,
  warnings as errors, deterministic builds, central package management, locked
  dependencies and a pinned SDK.
- Internal ZooKeeper Jute primitives: checked big-endian integers, booleans,
  strings, buffers and vectors, with explicit protocol limits.
- Internal `Stat` and request/reply header wire records.
- Internal request/response bodies for node, ACL, transaction and control
  messages, with strict handshake, result-shape and transaction-terminator checks.
- Connection-string parsing, upstream-compatible path validation and bounded
  chroot translation for requests, results, notifications and restoration paths.
- Bounded asynchronous ZooKeeper frame reader and writer with validated frame
  and buffer limits.
- Initial public contract types and an unshipped public API baseline: endpoints,
  create options and modes, ACLs and permissions, node data/stat/children and
  version records, request outcomes and operation records, client identity,
  watch removal results, and the ZooKeeper exception hierarchy with multi
  diagnostics.
- Protocol tests using xUnit v3, NSubstitute stream fault injection and Apache
  Java/Jute golden bytes for primitive and record encoding, plus framing and
  malformed-input coverage.
- Contract tests for the initial public types.
- Gradle-managed Apache Java fixtures for every implemented opcode and all
  UTF-16 path code units, with request verification in both languages.
- Bounded scripted peers and loopback fake-server tests for fragmented,
  delayed, partial and dropped messages, plus an initial codec allocation check.
- Apache ZooKeeper 3.8.7 and 3.9.6 local Compose fixtures pinned by image
  digest, with `just` lifecycle commands and a two-version smoke matrix that
  verifies the exact server version and authenticated readiness.
- Project documentation: README, driver specification, implementation plan and
  acceptance scenarios, research baseline, foundation ADR and third-party
  notices.
