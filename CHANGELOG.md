# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

ZkLink is in initial development. No version has been released, so the entries
below describe work in progress rather than supported behavior. TTL creation, auxiliary operation APIs, watch
delivery, a NuGet package, Native AOT validation and runtime performance data
remain unavailable. See
[README.md](README.md) for the current status and verified scope.

## [Unreleased]

### Added

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
  returned path/stat and chroot translation; TTL creation fails locally for now.
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
