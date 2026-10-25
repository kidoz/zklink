# Current capabilities

This describes the checkout as of **2026-10-26**. No package has been released,
and TTL creation, public transactions and watch delivery remain unavailable.

## Code and verification

| Area | Available now | Remaining scope |
| --- | --- | --- |
| Build | .NET 10, C# 14, deterministic builds, locked dependencies, warnings as errors | Release package metadata, license selection, package qualification |
| Protocol | Internal codecs for all 27 implemented opcodes, handshake/multi/framing, path/chroot translation and Gradle-managed Java fixtures | Public transaction/auxiliary APIs and remaining live qualification |
| Public types | Immutable snapshots, endpoints, create options, ACLs, versions, request failures, watch-removal results; contract tests | Public transaction and active-watch contracts |
| Sessions and operations | Connect/authenticate/sync, snapshots/subscriptions, heartbeat, same-session recovery, cancellation, close and node/ACL methods | TTL creation, public transactions, watches and broader live recovery under faults |
| Watches | Removal enums and result type only | Handles, registration, delivery, removal, restoration |
| Security | TCP/TLS, optional custom roots/client certificates, digest replay, explicit plaintext override and live ACL tests | Live secure ZooKeeper, additional identity schemes and cross-platform security qualification |
| Local servers | Pinned three-node Compose fixtures for 3.8.7 and 3.9.6 | Secure server and broader fault fixtures |
| Native AOT | Planned requirement | Publish and direct execution evidence for consumers |
| Performance | Initial small GetData codec allocation check | Session/load benchmarks and runtime performance evidence |

## Frameworks and platforms

The current target framework is `net10.0`; the pinned SDK is `10.0.402`. The core
has no third-party runtime dependencies. Public API declarations are checked
against shipped/unshipped baseline files; the shipped baseline is empty.

The [build workflow](../../.github/workflows/build.yml) defines Linux, Windows,
and macOS checks. Local macOS arm64 execution is recorded in the repository;
remote workflow results remain unverified. Proposed Native AOT targets are
`linux-x64`, `linux-arm64`, `win-x64`, and `osx-arm64`; none is qualified yet.

## Server targets

| ZooKeeper version | Intended role | Fixture settings | Driver compatibility |
| --- | --- | --- | --- |
| 3.8.7 | Required qualification target | [Version settings](../../dev/zookeeper-3.8.7.env) | Connection smoke and node/ACL tests passed |
| 3.9.6 | Required qualification target and development default | [Version settings](../../dev/zookeeper-3.9.6.env) | Connection smoke and node/ACL tests passed |

The [project overview](../../README.md#local-zookeeper-testing) records fixture
smoke checks on 2026-10-25 for both versions: three-node startup, exact-version
verification, authenticated create/read/delete, and cleanup. Those readiness checks use
the Java client in each image. `just zk-driver-matrix` additionally passed new
session, digest auth, sync and acknowledged close through ZkLink on every node.
`just zk-operations-matrix` adds live .NET CRUD/stat/children, version conflicts,
null/empty/large data, create modes, chroot, ACL checks and acknowledged-close
ephemeral deletion, plus the CRUD example on each node. This remains scoped
operation evidence rather than full driver qualification.

TLS is required by default. Loopback tests cover trusted/custom roots, wrong
identity, unknown issuer, expiry, mutual authentication, missing required client
certificate and plaintext rejection on local macOS arm64. The local fixtures use plaintext. SASL and read-only
mode are outside the planned initial client scope; coordination recipes and
administration require separate designs.

[Reference](README.md) · [Documentation home](../README.md)
