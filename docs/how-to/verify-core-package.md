# Verify a local core package

Run package inspection from the repository root with .NET SDK **10.0.403** and
Python 3.12 or newer. Docker, Bash and OpenSSL are unnecessary for this command:

```sh
just package-inspect
```

Or invoke the portable command directly, including on Windows:

```sh
python dev/prepare-core-package.py
```

The command builds a uniquely versioned `0.0.0-local.*` candidate and companion
symbol package under `artifacts/packages/run.*/feed/`. It never publishes them.
Missing prerequisites and failed checks return a nonzero exit code. To inspect a
specific release candidate, use `python dev/prepare-core-package.py 0.1.0`. A plain
`dotnet pack src/ZkLink -c Release` uses the core project's version and omits the
consumer checks.

The check inspects the package for the core `net10.0` assembly, XML API
documentation, package README, MIT license and third-party notices. Unexpected
assets and runtime package dependencies are rejected. Assembly references must
belong to the installed .NET runtime. SDK package validation and the existing
public API analyzer run during the build. No released-package compatibility
baseline exists yet. The core's public API baseline records the
initial 0.1.0 release.

The symbol check verifies the portable PDB identity and checksum against the DLL,
repository revision and Source Link mapping, and the checksum of every embedded
source document. Source Link identifies the base Git revision; embedded sources
preserve the exact candidate, including local working-tree changes. This checks
metadata locally; it does not prove remote source or symbol-server availability.

A standalone copy of the basic sample sources compiles with a `PackageReference`
to this candidate. A fresh package cache, exact version constraint and local-only
package source prevent reuse of an older candidate. Its generated dependency
lock is restored again in locked mode. The check requires package-only compile
and runtime assets, compares the output DLL with the package, and verifies the
actually loaded assembly hash without connecting. The consumer's exact
`--package-check` mode exits after this identity check, before reading connection
settings or credentials. An incorrect expected assembly hash must fail.

The `package-consumer.json` receipt records host, revision, source inputs and
hashes of the package, symbols, installed assemblies, dependency files and local
NuGet configuration. Inputs must stay unchanged during inspection. The live
runner rechecks these identities before starting any fixture.

## Run installed-package examples against servers

With Docker and Compose, Bash and OpenSSL also installed, run:

```sh
just package-check
```

This command first runs the same package inspection, then starts its live
fixtures. `bash dev/test-core-package.sh 0.1.0` verifies a specific release
candidate. The release workflow publishes the exact package and symbols that
this complete command verifies. Failed fixture cleanup returns a nonzero exit
code.

The installed consumer runs node operations, transactions, auxiliary queries and
one-shot, persistent and recursive watch examples against every node of isolated
three-node ZooKeeper **3.8.7** and **3.9.6** ensembles. Each case uses fresh digest
credentials, explicitly selected local plaintext, disposable ephemeral nodes,
a finite deadline and acknowledged close. Each server's exact version and
authenticated readiness are checked. Final authenticated probes, server logs
and removal of the owned containers and volumes run on success and failure.

Altered local artifacts also verify rejection of extra assets, dependencies,
wrong versions, damaged symbols, incorrect source links/checksums, source-project
resolution and mismatched assembly hashes. These failures are expected checks.

## Inspect evidence

`inspection-result.txt` records successful package-only checks. The complete
live command also requires `result.txt`, per-server execution/readiness logs and
fixture cleanup. Both retain `validation.json`, `package-consumer.json`,
build/restore logs, consumer dependency files and rejection logs.

The [build workflow](../../.github/workflows/build.yml) defines Docker-free
inspection on Linux, Windows and macOS and retains each candidate and its
evidence separately. The Linux live package job still runs against both server
versions. The [2026-10-29 CI run](https://github.com/kidoz/zklink/actions/runs/40882134428)
passed all three OS inspection jobs and the six Linux live-consumer cases.
Downloaded receipts were reviewed against package, symbol, installed assembly
and dependency-file hashes, positive identity checks and expected rejection
logs. Final authenticated readiness and fixture cleanup also passed. These
checks do not establish TLS
package-consumer coverage, Native AOT support or full release readiness.
Optional DI and analyzer packages have separate qualification requirements.

[How-to guides](README.md) · [Documentation home](../README.md)
