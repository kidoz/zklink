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

For codecs and stream framing:

```sh
just test tests/ZkLink.Protocol.Tests
```

Both suites are server-free. Use `--project` with `dotnet test`; this repository
selects Microsoft.Testing.Platform in [global.json](../../global.json).

## Compare primitive bytes with Apache Java

On macOS or Linux, install JDK 17 or later, curl, and Python 3 in addition to the
pinned .NET SDK. Then run:

```sh
bash dev/test-jute-oracle.sh
```

Require the final Java generation and C#-to-Java verification success message.
The script checks the downloaded jar's hash and build commit, compares generated
Java bytes with the checked-in fixture, and verifies C# bytes with Java. Inspect
`artifacts/jute-oracle/` if it fails. It does not replace the checked-in fixture;
investigate byte differences before making an intentional fixture update.

See the [oracle documentation](../../tests/ZkLink.JavaOracle/README.md) for the
exact records and provenance, and [verification scope](../explanation/verification.md)
for the limits of these checks.

[How-to guides](README.md) · [Documentation home](../README.md)
