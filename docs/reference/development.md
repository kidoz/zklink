# Development reference

## Build configuration

| Setting | Value | Source |
| --- | --- | --- |
| SDK | 10.0.402; roll-forward disabled | [global.json](../../global.json) |
| Target and language | `net10.0`, C# 14 | [Directory.Build.props](../../Directory.Build.props) |
| Compiler policy | Nullable enabled; warnings as errors | [Directory.Build.props](../../Directory.Build.props) |
| Formatter | CSharpier 1.3.0 | [Local tool manifest](../../dotnet-tools.json) |
| Dependency versions | Central versions and per-project lock files | [Directory.Packages.props](../../Directory.Packages.props) |
| Test runner | Microsoft.Testing.Platform | [global.json](../../global.json) |
| Test stack | xUnit v3 and NSubstitute | [Directory.Packages.props](../../Directory.Packages.props) |
| Packaging | `IsPackable=false`; no released package | [Directory.Build.props](../../Directory.Build.props) |

## Commands

Commands run from the repository root. Optional arguments below use brackets as
notation; omit the brackets when invoking a command.

| Command | Effect |
| --- | --- |
| `just` | List recipes |
| `just tools` | Restore local .NET tools |
| `just restore` | Restore tools and solution dependencies |
| `just build [configuration]` | Build the solution; default `Release` |
| `just format` | Restore tools and format C# and project XML |
| `just format-check` | Restore tools and check formatting |
| `just check` | Check formatting and build; does not run tests |
| `just test <project>` | Run a selected test project in Release mode |
| `bash dev/test-jute-oracle.sh` | Compare Java and C# primitive/record bytes; macOS/Linux, JDK 17+, curl, Python 3 |
| `just zk-check [version]` | Validate Compose configuration |
| `just zk-up [version]` | Start three nodes and wait up to 90 seconds for readiness |
| `just zk-status [version]` | Show health and host ports |
| `just zk-logs [version]` | Print server logs |
| `just zk-down [version]` | Remove the selected ensemble and its volumes |
| `just zk-matrix` | Smoke-test both pinned versions with isolated projects, retained logs, and cleanup; requires Bash |

ZooKeeper recipes default to `3.9.6`; `3.8.7` is the other checked-in choice.
Definitions are in the [justfile](../../justfile).

## Test projects

| Project | Scope | External prerequisites |
| --- | --- | --- |
| [ZkLink.Tests](../../tests/ZkLink.Tests/ZkLink.Tests.csproj) | Endpoints, model ownership, create options, failure contracts | Pinned .NET SDK |
| [ZkLink.Protocol.Tests](../../tests/ZkLink.Protocol.Tests/ZkLink.Protocol.Tests.csproj) | Primitive/record fixtures, malformed input, framing and stream failures | Pinned .NET SDK |
| [ZkLink.JavaOracle](../../tests/ZkLink.JavaOracle/README.md) | Optional Apache Java fixture generation and decoding of C# bytes | JDK 17+, curl, Python 3, pinned .NET SDK, network access |

The .NET suites need neither a server nor Java. No driver integration test
project exists yet.

## ZooKeeper fixture settings

[compose.yaml](../../compose.yaml) defines `zoo1`, `zoo2`, and `zoo3`. Each node
has container client port 2181, an authenticated readiness probe, and a loopback
host binding. Both versions use plaintext.

| Version | Settings | Default Compose project | Default host ports |
| --- | --- | --- | --- |
| 3.8.7 | [Environment file](../../dev/zookeeper-3.8.7.env) | `zklink-zookeeper-3-8-7` | 22181, 22182, 22183 |
| 3.9.6 | [Environment file](../../dev/zookeeper-3.9.6.env) | `zklink-zookeeper-3-9-6` | 2181, 2182, 2183 |

| Variable | Meaning |
| --- | --- |
| `COMPOSE_PROJECT_NAME` | Isolates a manual ensemble's containers, network, and volumes |
| `ZOOKEEPER_PORT_1`, `ZOOKEEPER_PORT_2`, `ZOOKEEPER_PORT_3` | Override host client ports |
| `ZOOKEEPER_IMAGE` | Overrides the image for manual lifecycle commands; checked-in files pin exact versions and digests |

The matrix runner ignores inherited image overrides, allocates dynamic host
ports, and assigns unique project names. Its printed artifact directory contains
Docker versions, revision/working-tree details, resolved Compose settings,
startup and server logs, exact-version output, status, and cleanup logs.

## Repository layout

| Path | Contents |
| --- | --- |
| `src/ZkLink/` | Library, XML API documentation, and API baselines |
| `tests/` | Server-free .NET suites and optional Java oracle |
| `dev/` | Versioned server settings, health checks, and verification scripts |
| `docs/` | Tutorials, how-to guides, reference, and explanation |
| `.github/workflows/` | Build and server-free test workflow |

[Reference](README.md) · [Documentation home](../README.md)
