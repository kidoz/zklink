# ZkLink

[![Language](https://img.shields.io/badge/language-C%23-512BD4)](https://learn.microsoft.com/dotnet/csharp/)
[![.NET SDK](https://img.shields.io/badge/.NET%20SDK-10.0.403-512BD4)](https://github.com/kidoz/zklink/blob/main/global.json)
[![License](https://img.shields.io/badge/license-MIT-green)](https://github.com/kidoz/zklink/blob/main/LICENSE)

ZkLink is a managed .NET client for Apache ZooKeeper: a first-class C# implementation of the ZooKeeper client TCP protocol, targeting .NET 10. Native AOT compatibility is a planned, separately tested requirement, not a current capability.

**Status: under development.** Internal protocol codecs, handshake records, transactions, connection-string/path/chroot handling, immutable result models and request-failure contracts are implemented and tested. A connectable client now provides TCP/TLS, digest authentication, session recovery, state subscriptions, bounded requests, `SyncAsync` and acknowledged close. Node CRUD, stat, children and ACL operations are now available. Typed write transactions, read batches, session ephemeral queries, descendant counts and identity queries are available. TTL creation is available with explicit client opt-in and server enablement; one-shot watched reads and persistent/recursive subscriptions are available, with removal, same-session restoration and explicit persistent reconnect gaps. An optional `AddZkLink` helper provides host-owned dependency injection with server-free lifecycle tests. Six optional Roslyn analyzers cover literal credentials, unsafe plaintext digest, watch ownership and asynchronous usage, with compilation and packed-consumer tests.

## Current repository state

- **Solution:** `ZkLink.slnx` with the core, dependency-injection and analyzer libraries, five .NET test projects and a runnable connection sample.
- **Target:** `net10.0`, C# 14, nullable reference types, and warnings as errors.
- **Code:** checked protocol encoding/decoding for node, ACL, transaction and control messages; bounded asynchronous framing; new/resume handshake records; connection-string parsing and namespace translation. Public supporting types include immutable results, create options and failure/removal contracts. One explicit session per client, negotiated-timeout heartbeats, endpoint rotation, same-session reconnect, cancellation outcomes and close cleanup are implemented. Sent application requests are never replayed automatically.
- **Tests:** xUnit suites cover Apache Java fixtures for all implemented opcodes, transaction success/error shapes, every reply split and truncation, all UTF-16 path code units, chroot boundaries and supporting public contracts. A bounded scripted stream and loopback fake server exercise delayed delivery, partial writes, dropped replies and handshake exchanges. Virtual-time tests exercise session and request lifecycles. Loopback TLS tests check chain/name validation, certificate expiry and mutual authentication. The managed connection sample has passed digest auth, sync and acknowledged close on every node of both pinned versions. Live .NET tests cover CRUD, versions, null/empty and 1,000,000-byte data, create modes, chroot, ACL authorization and acknowledged-close ephemeral deletion on both pinned versions. Live transaction tests verify atomic success/rollback, independent read errors, and committed writes whose replies are dropped, followed by same-session resume without replay. Auxiliary tests verify session-scoped textual ephemeral prefixes, chroot, recursive counts, permissions and server identities. One-shot watch tests cover events, sharing/removal, chroot, re-registration and same-session restoration after a connection cut. Persistent/recursive watch tests cover repeated delivery, profile-specific removal and reconnect gaps. Broader live faults remain unverified.
- **Build:** SDK 10.0.403 is pinned, with central dependency versions and lock files; core has no third-party runtime dependencies. Public API changes are checked against nullable baselines. The [build workflow](.github/workflows/build.yml) defines Linux/Windows/macOS checks and separate Gradle-based Apache Java verification. Only local macOS arm64 execution has been verified; remote runs and Native AOT remain unverified.
- **Distribution:** no package has been released. The project is [MIT-licensed](LICENSE); remaining release packaging and qualification remain open. See the [dependency inventory](THIRD-PARTY-NOTICES.md).

The library lives in `src/`; `tests/` contains public-contract tests, protocol tests and an optional Java oracle. The [basic sample](samples/ZkLink.Samples.Basic) can connect, exercise CRUD with `--nodes`, run transactions with `--transactions`, and query ephemerals/counts/identities with `--auxiliary`, and exercise one-shot, persistent and recursive watches with `--watches`; benchmarks are not yet available.

## Development commands

Install [just](https://just.systems/) and .NET SDK **10.0.403**. Docker with Compose is required for the ZooKeeper fixture. Run `just` to list shortcuts.

| Command | Purpose |
| --- | --- |
| `just restore` | Restore local tools and solution dependencies |
| `just build` | Build the solution in Release mode |
| `just format` | Format C# and project XML with CSharpier |
| `just format-check` | Check formatting without changing files |
| `just check` | Check formatting and build |
| `just test tests/ZkLink.Protocol.Tests` | Run the server-free protocol suite |
| `just test tests/ZkLink.Tests` | Run the public model and failure-contract suite |
| `just test tests/ZkLink.Extensions.DependencyInjection.Tests` | Run the server-free DI registration and host-lifetime suite |
| `just test tests/ZkLink.Analyzers.Tests` | Run the Roslyn diagnostic suite |
| `bash dev/test-analyzers-package.sh` | Pack and verify the analyzer with a local consumer |
| `bash dev/test-jute-oracle.sh` | Use the Gradle wrapper to compare Java fixtures and verify C# requests |
| `just zk-check [version]` | Validate the selected ZooKeeper configuration |
| `just zk-up [version]` | Start the selected ensemble and wait for readiness |
| `just zk-status [version]` | Show container health and endpoints |
| `just zk-logs [version]` | Print server logs |
| `just zk-down [version]` | Remove the selected ensemble and its data volumes |
| `just zk-matrix` | Smoke-test both pinned server versions and clean up |
| `just zk-driver-matrix` | Also connect/authenticate/sync/close using ZkLink on every node |
| `just zk-peer-partition-faults` | Isolate a follower’s peer network and verify majority progress, session/watch recovery and healing on both versions |
| `just zk-partition-faults` | Discard each direction of client traffic independently and verify recovery or local loss on both versions |
| `just zk-quorum-faults` | Stop two voters and verify valid-session recovery or local loss, no replay and cleanup on both versions |
| `just zk-leader-faults` | Stop the observed leader and verify replacement election, session/watch recovery and rollback on both versions |
| `just zk-follower-faults` | Stop one observed follower and verify session/watch recovery, request outcomes and rollback on both versions |
| `just zk-security-matrix` | Verify client TLS/mTLS operations, certificate rejection and secure watch recovery on both versions |
| `just zk-security-concurrency` | Run bounded concurrent TLS rejection rounds with valid controls on both versions |
| `just zk-ttl-matrix` | Test TTL-enabled and TTL-disabled servers on both versions |
| `just zk-operations-matrix` | Run live node/ACL/transaction/auxiliary/watch tests on both versions and examples on each node |

CSharpier **1.3.0** is pinned in [dotnet-tools.json](dotnet-tools.json). The format shortcuts restore the local tool automatically. Direct equivalents are `dotnet tool restore`, `dotnet csharpier format .`, and `dotnet csharpier check .`.

The test runner is Microsoft.Testing.Platform: use `dotnet test --project tests/ZkLink.Protocol.Tests -c Release`. For reproducible dependency checks, run `dotnet restore ZkLink.slnx --locked-mode`. The optional [Apache Java reference utility](tests/ZkLink.JavaOracle/README.md) uses the pinned Gradle wrapper and a JDK; no global Gradle installation is required. Ordinary .NET tests use checked-in fixtures and require no Java or ZooKeeper installation.

## Documentation

Start with the [ZkLink documentation](docs/README.md), organized using [Diátaxis](https://diataxis.fr/):

- [Tutorials](docs/tutorials/README.md) — build, connect and exercise node operations.
- [How-to guides](docs/how-to/README.md) — run development checks and manage a local ZooKeeper ensemble.
- [Reference](docs/reference/README.md) — current capabilities, commands, and supporting public types.
- [Explanation](docs/explanation/README.md) — protocol boundaries, data ownership, and verification scope.

## Server compatibility target

The intended server targets are **ZooKeeper 3.8.7 and 3.9.6**. These versions were selected from the [Apache release page](https://zookeeper.apache.org/releases/) in the **2026-10-25** research snapshot. Both must pass driver integration and release checks before support is advertised. Connection/auth/sync/close and node/ACL/transaction/auxiliary operations have live tests on both versions; full driver compatibility remains unqualified.

| Server | Role | Pinned Compose settings | Default host ports |
| --- | --- | --- | --- |
| 3.8.7 | Required stable-branch target | [3.8.7 settings](dev/zookeeper-3.8.7.env) | 22181–22183 |
| 3.9.6 | Required current-branch target; development default | [3.9.6 settings](dev/zookeeper-3.9.6.env) | 2181–2183 |

## Local ZooKeeper testing

[compose.yaml](compose.yaml) starts a three-node ensemble. The `just` lifecycle commands accept `3.8.7` or `3.9.6`, defaulting to **3.9.6**. Both official images are pinned by multi-platform digest in the version-specific settings above; each version uses a separate Compose project and host ports.

```sh
just zk-up
just zk-status
dotnet run --project samples/ZkLink.Samples.Basic -c Release -- 127.0.0.1:2181 --plaintext
just zk-logs > /tmp/zklink-zookeeper.log
just zk-down
```

For 3.8.7, use `just zk-up 3.8.7`, `just zk-status 3.8.7`, `just zk-logs 3.8.7`, and `just zk-down 3.8.7`. The direct Compose equivalent uses `--env-file dev/zookeeper-3.8.7.env` on each command. Always select the same version for startup, logs, and cleanup.

Connect to `127.0.0.1:2181,127.0.0.1:2182,127.0.0.1:2183` for 3.9.6 or `127.0.0.1:22181,127.0.0.1:22182,127.0.0.1:22183` for 3.8.7. Each container becomes healthy after an authenticated create/read/delete probe with fresh credentials and a disposable ephemeral node. Both fixtures use plaintext; ZkLink consumers must explicitly disable TLS for them. Client TLS/mTLS fixtures are available separately through `just zk-security-matrix`. Authentication-required server fixtures, additional operation coverage and broader fault recovery qualification remain planned.

`just zk-matrix` runs both versions sequentially with unique project names and dynamically assigned loopback ports. It checks the exact server version and authenticated readiness on all three nodes, records configuration, server logs, version output, and container status in a printed temporary artifact directory, and removes its containers and volumes on success or failure. It fails if Docker, either image, readiness, version verification, or cleanup fails. The runner uses the checked-in image pins regardless of an inherited `ZOOKEEPER_IMAGE` override.

For additional concurrent manual runs, set a unique `COMPOSE_PROJECT_NAME` and unused `ZOOKEEPER_PORT_1`, `ZOOKEEPER_PORT_2`, and `ZOOKEEPER_PORT_3`. Preserve logs before cleanup on failure. `ZOOKEEPER_IMAGE` remains available for explicit image overrides outside the matrix runner. Update image pins and the support matrix together when qualifying new patches.

Fixture validation on **2026-10-25**: `just zk-matrix` passed for **both 3.8.7 and 3.9.6**, including three-node startup, exact-version verification, authenticated create/read/delete, and cleanup. The earlier quorum-loss rejection check covered 3.9.6 only. `just zk-driver-matrix` also passed new session, digest authentication, sync and acknowledged close through ZkLink on all six nodes. These are plaintext connection smoke checks, not full operation/security/recovery qualification.

Live operation validation on **2026-10-26**: `just zk-operations-matrix` checks managed CRUD/stat/children, version conflicts, create modes, chroot, ACL permissions and ephemeral removal after acknowledged close on both patches. The CRUD sample also runs on all six nodes. See the [node tutorial](docs/tutorials/use-node-operations.md) and [node reference](docs/reference/nodes.md) for usage and exact scope.

Transaction validation on **2026-10-26** adds rollback, per-member read errors and a real commit/drop-reply/reconnect test on both versions. The transaction sample runs on every node. See [Execute a transaction](docs/tutorials/use-transactions.md) and the [transaction reference](docs/reference/transactions.md).

Auxiliary operation validation on **2026-10-26** adds session-scoped ephemeral prefixes, recursive counts, chroot, permission errors and authenticated/anonymous identities on both versions. The `--auxiliary` sample runs on every node. See the [auxiliary reference](docs/reference/auxiliary-operations.md).

TTL validation on **2026-10-26** adds explicit client opt-in, both create modes, atomic creation, chroot, maximum TTL, close survival, child protection, eventual cleanup and disabled-server errors on both versions. Run `just zk-ttl-matrix`; see [TTL node creation](docs/reference/ttl-nodes.md).

One-shot watch validation on **2026-10-26** adds buffered event delivery, shared/confirmed removal, chroot, re-registration, close and same-session restoration on both versions. Controlled peers cover cancellation, overflow and 10,000-watch restoration. See [Watch a node once](docs/tutorials/watch-a-node.md) and the [watch reference](docs/reference/watches.md).

Persistent and recursive watch validation adds repeated delivery, independent
removal behavior, extended restoration and reconnect gaps on both versions.
The default 3.8 profile rejects conflicting modes at one path; the 3.9 profile
permits independent modes there.
Recovery validation also covers interrupted restoration, rejected/time-limited
resets and terminal stream completion. Both live versions verify a dropped
restore acknowledgment followed by same-session retry, plus local loss during
paused reconnect. See [Watch repeated changes](docs/tutorials/watch-repeated-changes.md).

Client TLS and mutual TLS verification covers trusted .NET operations on every
node, digest and X.509 ACL distinctions, transactions, watch restoration and
certificate rejection. Run `just zk-security-matrix`; see
[Verify secure connections](docs/how-to/verify-secure-connections.md).

Built-in [tracing and metrics](docs/reference/telemetry.md) report lifecycle and
operation outcomes, request pressure, heartbeat and watch/event state. Diagnostic
delivery is bounded and asynchronous, with redaction and listener-isolation checks.

[Follower-outage checks](docs/how-to/verify-follower-recovery.md) verify recovery
after an actual server stop on both pinned versions, including session/ephemeral
preservation, watch readiness and no replay of an uncertain create.
[Leader-outage checks](docs/how-to/verify-leader-recovery.md) additionally verify
a replacement election and the old leader returning as a follower.
[Quorum-outage checks](docs/how-to/verify-quorum-recovery.md) cover brief recovery
and terminal local loss after a longer interruption.
[One-way traffic checks](docs/how-to/verify-partition-recovery.md) cover both
directions independently, with accurate request outcomes, no delayed forwarding
or replay, and recovery or terminal local loss.
[Peer partition checks](docs/how-to/verify-peer-partition-recovery.md) isolate a
follower's peer interface while preserving its process and client network. Both
pinned versions verify majority progress, same-session/watch recovery, no replay
and consistent data across all members after healing.

## Not yet available

A NuGet package, Native AOT validation and runtime performance data are not available. Client TLS/mTLS has scoped live evidence on both pinned versions with a macOS arm64 client. Revocation services, certificate rotation, quorum TLS and cross-platform security qualification remain open.
