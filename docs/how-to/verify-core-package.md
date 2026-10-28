# Verify a local core package

Run from the repository root with .NET SDK **10.0.403**, Docker with Compose,
Python 3 and OpenSSL installed:

```sh
just package-check
```

The command builds a uniquely versioned `0.0.0-local.*` candidate and companion
symbol package under `artifacts/packages/run.*/feed/`. It never publishes them.
Missing prerequisites, failed checks and failed fixture cleanup return a nonzero
exit code. To verify a specific release candidate, use
`bash dev/test-core-package.sh 0.1.0`. The release workflow publishes the exact
package and symbols that this command verifies. A plain
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
actually loaded assembly hash before connecting.

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

Inspect the run directory for `result.txt`, `validation.json`, build/restore logs,
consumer dependency files, rejection logs and per-server execution/cleanup
evidence. A separate [CI job](../../.github/workflows/build.yml) retains the
candidate and this evidence. Local execution does not establish remote CI results,
cross-platform package qualification, TLS package-consumer coverage, Native AOT
support or release readiness. Optional DI and analyzer packages have separate
qualification requirements.

[How-to guides](README.md) · [Documentation home](../README.md)
