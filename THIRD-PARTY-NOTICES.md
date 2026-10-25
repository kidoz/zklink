# Third-party dependency inventory

Snapshot: 2026-10-25. The core library has **no third-party runtime dependencies**. No source from another driver was copied. Project license selection and package publication remain pending; this inventory does not relicense upstream material.

The resolved dependency graphs and integrity hashes are in the project `packages.lock.json` files. License expressions below were read from the restored NuGet package manifests. NuGet packages retain their upstream license/notice files in the package cache; they are not redistributed by this source change.

| Dependency | Pinned version | Scope | License |
| --- | --- | --- | --- |
| CSharpier | 1.3.0 | Local formatting tool | MIT |
| Microsoft.CodeAnalysis.PublicApiAnalyzers | 5.6.0 | Build analyzer only; private assets | MIT |
| NSubstitute | 6.2.0 | Test only | BSD-3-Clause |
| Castle.Core | 5.1.1 | Transitive test only | Apache-2.0 |
| xunit.v3.mtp-v2, xunit.v3.assert, xunit.v3.common, xunit.v3.core.mtp-v2, xunit.v3.extensibility.core, xunit.v3.runner.common, xunit.v3.runner.inproc.console | 4.0.2 | Test only | Apache-2.0 |
| xunit.analyzers | 2.2.0 | Test analyzer | Apache-2.0 |
| Microsoft.Testing.Platform, Microsoft.Testing.Platform.MSBuild, Microsoft.Testing.Extensions.Telemetry, Microsoft.Testing.Extensions.TrxReport.Abstractions | 2.5.1 | Transitive test only | MIT |
| Microsoft.ApplicationInsights | 2.23.0 | Transitive test only | MIT |
| Microsoft.Bcl.AsyncInterfaces, System.Diagnostics.EventLog | 6.0.0 | Transitive test only | MIT |
| Microsoft.Win32.Registry | 5.0.0 | Transitive test only | MIT |
| System.Security.AccessControl | 6.0.1 | Transitive test only | MIT |
| Apache ZooKeeper Jute and Java client | 3.9.6 | Optional Java fixture generator/verifier | Apache-2.0 |
| SLF4J API | 2.0.17 | Test-only Java logging API; no logging provider loaded | MIT |
| Gradle wrapper | 9.8.2 | Java reference build tool | Apache-2.0 |

Apache ZooKeeper is a project of the Apache Software Foundation. Its Jute schema and archive behavior define the wire format. The fixture utilities use published Jute and client classes; no ZooKeeper implementation source or jar is checked into this repository. Gradle downloads the jars into its dependency cache. The upstream source license and notice are preserved under `tests/ZkLink.JavaOracle/licenses/` at the same pinned commit; the Maven Jute jar itself has no license/notice entries. Fixture data is produced by the original utilities under `tests/ZkLink.JavaOracle/src/main/java/`.

Gradle wrapper scripts and their bootstrap jar are checked in using the standard generated wrapper, preserving their upstream license headers. Its distribution checksum is pinned. Exact Java dependencies are locked in `gradle.lockfile`, and jar/POM integrity hashes are in `gradle/verification-metadata.xml`. These tools and dependencies are not included in the managed driver.

Java tooling provenance, artifact hash, source commit, exact fixture semantics and reproduction commands are in [the fixture guide](tests/ZkLink.JavaOracle/README.md). Review upstream notices and final redistribution assets again before packaging. No Apache endorsement is implied.
