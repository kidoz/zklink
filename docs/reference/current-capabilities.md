# Current capabilities

This describes the checkout as of **2026-10-25**. No package has been released,
and there is no usable ZooKeeper client yet.

## Code and verification

| Area | Available now | Remaining scope |
| --- | --- | --- |
| Build | .NET 10, C# 14, deterministic builds, locked dependencies, warnings as errors | Release package metadata, license selection, package qualification |
| Protocol | Internal Jute primitives, Stat/header records, bounded asynchronous framing; server-free tests and primitive Java fixtures | Full operation encoding, handshake, dispatch, multi, live interoperability |
| Public types | Immutable snapshots, endpoints, create options, ACLs, versions, request failures, watch-removal results; contract tests | Connectable client and configuration for runtime behavior |
| Sessions and operations | Operation identifiers and failure metadata only | Connections, heartbeats, reconnects, node operations, transactions, chroot/path handling |
| Watches | Removal enums and result type only | Handles, registration, delivery, removal, restoration |
| Security | ACL representation only | TLS/mTLS, client authentication, secure transport |
| Local servers | Pinned three-node Compose fixtures for 3.8.7 and 3.9.6 | Driver integration and security fixtures |
| Native AOT | Planned requirement | Publish and direct execution evidence for consumers |
| Performance | No measurements published | Benchmarks and allocation evidence |

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
| 3.8.7 | Required qualification target | [Version settings](../../dev/zookeeper-3.8.7.env) | Unverified |
| 3.9.6 | Required qualification target and development default | [Version settings](../../dev/zookeeper-3.9.6.env) | Unverified |

The [project overview](../../README.md#local-zookeeper-testing) records fixture
smoke checks on 2026-10-25 for both versions: three-node startup, exact-version
verification, authenticated create/read/delete, and cleanup. Those checks use
the Java client in each image. They do not exercise ZkLink.

TLS is intended to be required by default in the future client. No TLS option
or transport exists today. The local fixtures use plaintext. SASL and read-only
mode are outside the planned initial client scope; coordination recipes and
administration require separate designs.

[Reference](README.md) · [Documentation home](../README.md)
