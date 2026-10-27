# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

ZkLink is in initial development. No version has been released, so the entries
below describe work in progress rather than supported behavior. A NuGet package, Native AOT validation and runtime performance data
remain unavailable. See
[README.md](README.md) for the current status and verified scope.

## [Unreleased]

### Fixed

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
