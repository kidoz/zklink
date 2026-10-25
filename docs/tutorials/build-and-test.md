# Build and test ZkLink

In this tutorial, you will build ZkLink and run its public-contract and protocol
tests. At the end, you will have a compiled library and test results from your
machine. This exercise requires no ZooKeeper server or Java installation.

## Before you begin

Have a local checkout of this repository, .NET SDK **10.0.402**, and `just`
installed. Open a terminal at the repository root, which contains `ZkLink.slnx`.
The first restore needs access to the configured NuGet feed.

## 1. Check the development tools

Run:

```sh
dotnet --version
just --list
```

The SDK output should be `10.0.402`. The recipe list includes `restore`, `build`,
and `test`. The repository pins the SDK with roll-forward disabled.

## 2. Restore tools and dependencies

```sh
just restore
dotnet restore ZkLink.slnx --locked-mode
```

The first command restores CSharpier and solution dependencies. The second
checks that dependency resolution agrees with the checked-in lock files.
Both commands should exit successfully before you continue.

## 3. Check formatting and build

```sh
just check
```

This checks C# and project XML formatting, then builds the solution in Release
mode. Expect a successful build without warnings: the repository treats warnings
as errors and checks public declarations against API baseline files.

## 4. Run the public-contract tests

```sh
just test tests/ZkLink.Tests
```

Expect a successful test summary with no failed tests. This suite checks endpoint
validation, copied data and collections, create-option constraints, and failure
metadata. It exercises supporting types without opening a connection.

## 5. Run the protocol tests

```sh
just test tests/ZkLink.Protocol.Tests
```

Expect another successful test summary. This suite checks Jute encoding against
checked-in Java fixtures and exercises fragmented frames, malformed input, and
stream failures, virtual-time session recovery and cancellation, and loopback
TLS/mTLS. It uses local fixtures and test doubles.

You have now built the library and exercised the behavior implemented in this
checkout. For repeatable checks after a change, use
[Run the development checks](../how-to/run-development-checks.md). To understand
what these results establish, read
[What the verification layers prove](../explanation/verification.md).

Continue with [Connect a session](connect-a-session.md) to use the compiled
client against the local ZooKeeper ensemble.

[Tutorials](README.md) · [Documentation home](../README.md)
