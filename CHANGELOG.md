# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

ZkLink is in initial development. No version has been released, so the entries
below describe work in progress rather than supported behavior. There is no
usable public client, NuGet package, TLS or authentication support, watch
implementation, Native AOT validation or performance data yet. See
[README.md](README.md) for the current status and verified scope.

## [Unreleased]

### Added

- Initial .NET 10 solution (`src/ZkLink`) with C# 14, nullable reference types,
  warnings as errors, deterministic builds, central package management, locked
  dependencies and a pinned SDK.
- Internal ZooKeeper Jute primitives: checked big-endian integers, booleans,
  strings, buffers and vectors, with explicit protocol limits.
- Internal `Stat` and request/reply header wire records.
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
- Apache ZooKeeper 3.8.7 and 3.9.6 local Compose fixtures pinned by image
  digest, with `just` lifecycle commands and a two-version smoke matrix that
  verifies the exact server version and authenticated readiness.
- Project documentation: README, driver specification, implementation plan and
  acceptance scenarios, research baseline, foundation ADR and third-party
  notices.
