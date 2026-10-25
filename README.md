# ZkLink

[![Language](https://img.shields.io/badge/language-C%23-512BD4)](https://learn.microsoft.com/dotnet/csharp/)
[![.NET SDK](https://img.shields.io/badge/.NET%20SDK-10.0.402-512BD4)](https://github.com/kidoz/zklink/blob/main/global.json)
[![License](https://img.shields.io/badge/license-TBD-lightgrey)](https://github.com/kidoz/zklink/blob/main/README.md#current-repository-state)

ZkLink is a proposed managed .NET client for Apache ZooKeeper: a first-class C# implementation of the ZooKeeper client TCP protocol, targeting .NET 10. Native AOT compatibility is a planned, separately tested requirement, not a current capability.

**Status: under development.** Internal Jute codecs and framing, immutable result models, create options and request-failure contracts are implemented and tested. There is no usable client yet; connections, sessions, node operations, watches and secure transport remain unimplemented.

## Current repository state

- **Solution:** `ZkLink.slnx` with `src/ZkLink`, `tests/ZkLink.Tests` and `tests/ZkLink.Protocol.Tests`.
- **Target:** `net10.0`, C# 14, nullable reference types, and warnings as errors.
- **Code:** internal checked Jute codecs and asynchronous framing; public immutable data/ACL/children/stat models, version conditions, create options, request-outcome exceptions and watch-removal results. These supporting types do not yet connect to a server.
- **Tests:** server-free xUnit suites check Java/Jute primitive bytes, fragmented/malformed frames, input snapshots, TTL boundaries, failure metadata and safe default formatting. Live node operations, transactions, path/chroot handling, session recovery and watch delivery remain untested.
- **Build:** SDK 10.0.402 is pinned, with central dependency versions and lock files; core has no third-party runtime dependencies. Public API changes are checked against nullable baselines. The [build workflow](.github/workflows/build.yml) runs formatting, locked restore, Release build and server-free tests on Linux, Windows and macOS. Only local macOS arm64 execution has been verified; remote runs and Native AOT remain unverified.
- **Distribution:** no package has been released. License selection, package metadata and release qualification remain open. See the [dependency inventory](THIRD-PARTY-NOTICES.md).

The library lives in `src/`; `tests/` contains public-contract tests, protocol tests and an optional Java oracle. Samples and benchmarks are not yet available.

## Development commands

Install [just](https://just.systems/) and .NET SDK **10.0.402**. Docker with Compose is required for the ZooKeeper fixture. Run `just` to list shortcuts.

| Command | Purpose |
| --- | --- |
| `just restore` | Restore local tools and solution dependencies |
| `just build` | Build the solution in Release mode |
| `just format` | Format C# and project XML with CSharpier |
| `just format-check` | Check formatting without changing files |
| `just check` | Check formatting and build |
| `just test tests/ZkLink.Protocol.Tests` | Run the server-free protocol suite |
| `just test tests/ZkLink.Tests` | Run the public model and failure-contract suite |
| `bash dev/test-jute-oracle.sh` | Regenerate/compare Java fixtures and verify C# bytes with Java |
| `just zk-check [version]` | Validate the selected ZooKeeper configuration |
| `just zk-up [version]` | Start the selected ensemble and wait for readiness |
| `just zk-status [version]` | Show container health and endpoints |
| `just zk-logs [version]` | Print server logs |
| `just zk-down [version]` | Remove the selected ensemble and its data volumes |
| `just zk-matrix` | Smoke-test both pinned server versions and clean up |

CSharpier **1.3.0** is pinned in [dotnet-tools.json](dotnet-tools.json). The format shortcuts restore the local tool automatically. Direct equivalents are `dotnet tool restore`, `dotnet csharpier format .`, and `dotnet csharpier check .`.

The test runner is Microsoft.Testing.Platform: use `dotnet test --project tests/ZkLink.Protocol.Tests -c Release`. For reproducible dependency checks, run `dotnet restore ZkLink.slnx --locked-mode`. The optional [Java oracle](tests/ZkLink.JavaOracle/README.md) requires JDK 17+, curl and Python 3; ordinary .NET tests use checked-in fixtures and require no Java/server.

## Documentation

Start with the [ZkLink documentation](docs/README.md), organized using [Diátaxis](https://diataxis.fr/):

- [Tutorials](docs/tutorials/README.md) — build the library and run your first tests.
- [How-to guides](docs/how-to/README.md) — run development checks and manage a local ZooKeeper ensemble.
- [Reference](docs/reference/README.md) — current capabilities, commands, and supporting public types.
- [Explanation](docs/explanation/README.md) — protocol boundaries, data ownership, and verification scope.

## Server compatibility target

The intended server targets are **ZooKeeper 3.8.7 and 3.9.6**. These versions were selected from the [Apache release page](https://zookeeper.apache.org/releases/) in the **2026-10-25** research snapshot. Both must pass driver integration and release checks before support is advertised. Driver interoperability remains unverified.

| Server | Role | Pinned Compose settings | Default host ports |
| --- | --- | --- | --- |
| 3.8.7 | Required stable-branch target | [3.8.7 settings](dev/zookeeper-3.8.7.env) | 22181–22183 |
| 3.9.6 | Required current-branch target; development default | [3.9.6 settings](dev/zookeeper-3.9.6.env) | 2181–2183 |

## Local ZooKeeper testing

[compose.yaml](compose.yaml) starts a three-node ensemble. The `just` lifecycle commands accept `3.8.7` or `3.9.6`, defaulting to **3.9.6**. Both official images are pinned by multi-platform digest in the version-specific settings above; each version uses a separate Compose project and host ports.

```sh
just zk-up
just zk-status
# Run integration tests here once the driver and test projects exist.
just zk-logs > /tmp/zklink-zookeeper.log
just zk-down
```

For 3.8.7, use `just zk-up 3.8.7`, `just zk-status 3.8.7`, `just zk-logs 3.8.7`, and `just zk-down 3.8.7`. The direct Compose equivalent uses `--env-file dev/zookeeper-3.8.7.env` on each command. Always select the same version for startup, logs, and cleanup.

Connect to `127.0.0.1:2181,127.0.0.1:2182,127.0.0.1:2183` for 3.9.6 or `127.0.0.1:22181,127.0.0.1:22182,127.0.0.1:22183` for 3.8.7. Each container becomes healthy after an authenticated create/read/delete probe with fresh credentials and a disposable ephemeral node. Both fixtures use plaintext; ZkLink consumers must explicitly disable TLS for them. TLS/mTLS, authentication-required fixtures, and driver compatibility tests remain planned.

`just zk-matrix` runs both versions sequentially with unique project names and dynamically assigned loopback ports. It checks the exact server version and authenticated readiness on all three nodes, records configuration, server logs, version output, and container status in a printed temporary artifact directory, and removes its containers and volumes on success or failure. It fails if Docker, either image, readiness, version verification, or cleanup fails. The runner uses the checked-in image pins regardless of an inherited `ZOOKEEPER_IMAGE` override.

For additional concurrent manual runs, set a unique `COMPOSE_PROJECT_NAME` and unused `ZOOKEEPER_PORT_1`, `ZOOKEEPER_PORT_2`, and `ZOOKEEPER_PORT_3`. Preserve logs before cleanup on failure. `ZOOKEEPER_IMAGE` remains available for explicit image overrides outside the matrix runner. Update image pins and the support matrix together when qualifying new patches.

Fixture validation on **2026-10-25**: `just zk-matrix` passed for **both 3.8.7 and 3.9.6**, including three-node startup, exact-version verification, authenticated create/read/delete, and cleanup. The earlier quorum-loss rejection check covered 3.9.6 only. These checks use the Java client supplied by each image; ZkLink driver tests remain unimplemented.

## Not yet available

There is no connectable client, NuGet package, TLS or authentication support, watch implementation, Native AOT validation, or performance data. Supporting public model and exception types do not establish driver interoperability.
