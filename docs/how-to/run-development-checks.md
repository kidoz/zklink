# Run the development checks

Use these checks to validate a change locally. Run commands from the repository
root with the pinned SDK and `just` installed.

## Run the checks used by the build workflow

```sh
dotnet restore ZkLink.slnx --locked-mode
dotnet tool restore
dotnet csharpier check .
dotnet build ZkLink.slnx -c Release --no-restore
dotnet test --project tests/ZkLink.Tests -c Release --no-build --no-restore
dotnet test --project tests/ZkLink.Protocol.Tests -c Release --no-build --no-restore
dotnet test --project tests/ZkLink.Extensions.DependencyInjection.Tests -c Release --no-build --no-restore
dotnet test --project tests/ZkLink.Analyzers.Tests -c Release --no-build --no-restore
bash dev/test-analyzers-package.sh
```

Require every command to exit successfully. These commands mirror the
[build workflow](../../.github/workflows/build.yml).

If formatting fails, run `just format`, review the resulting diff, and rerun the
format check. If locked restore fails after an intentional dependency change,
run `just restore`, review the affected package versions and lock files, then
repeat the checks above. For unexpected lock differences, check your checkout
and pinned SDK before updating dependencies.

If a public API analyzer fails the build, compare the declaration with
[the unshipped baseline](../../src/ZkLink/PublicAPI.Unshipped.txt). Update that
baseline only for an intended API change, preserving nullable annotations.

## Run one suite while iterating

For model, endpoint, and exception changes:

```sh
just test tests/ZkLink.Tests
```

For protocol, session, scheduler and transport changes:

```sh
just test tests/ZkLink.Protocol.Tests
```

For DI registration and Roslyn diagnostic changes:

```sh
just test tests/ZkLink.Extensions.DependencyInjection.Tests
just test tests/ZkLink.Analyzers.Tests
```

All four suites are server-free. Use `--project` with `dotnet test`; this repository
selects Microsoft.Testing.Platform in [global.json](../../global.json).

## Compare protocol bytes with Apache Java

On macOS or Linux, install JDK 25 and Python 3 in addition to the
pinned .NET SDK. Then run:

```sh
bash dev/test-jute-oracle.sh
```

Require the final Java generation and C#-to-Java verification success message.
The Gradle wrapper verifies its distribution and dependency checksums, then
generates primitive/opcode/path fixtures and verifies C# requests with Apache
classes. The script compares generated fixtures with checked-in bytes. Inspect
`artifacts/jute-oracle/` if it fails. It does not replace the checked-in fixture;
investigate byte differences before making an intentional fixture update.

See the [oracle documentation](../../tests/ZkLink.JavaOracle/README.md) for the
exact records and provenance, and [verification scope](../explanation/verification.md)
for the limits of these checks.

For real connection/auth/sync/close checks on all nodes of both pinned versions,
run `just zk-driver-matrix`. For node/ACL/transaction/auxiliary/watch verification and the examples on
both versions, run `just zk-operations-matrix`. Required live configuration and
prerequisites fail explicitly when absent. Use `just zk-security-matrix` for dedicated client TLS/mTLS verification;
broader operation fault tests remain open.

Run `just zk-ttl-matrix` separately for TTL-enabled and TTL-disabled servers on
both pinned versions. The ordinary operation suite excludes TTL and security fixture cases.
The security runner generates temporary certificates and retains only public
evidence; see [Verify secure connections](verify-secure-connections.md).

[How-to guides](README.md) · [Documentation home](../README.md)
