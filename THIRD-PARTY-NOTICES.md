# Third-party dependency inventory

Snapshot: 2026-10-28. The core library has **no third-party runtime dependencies**. No source from another driver was copied. The project is [MIT-licensed](LICENSE); package publication remains pending, and this inventory does not relicense upstream material.

The resolved dependency graphs and integrity hashes are in the project `packages.lock.json` files. License expressions below were read from the restored NuGet package manifests. NuGet packages retain their upstream license/notice files in the package cache; they are not redistributed by this source change.

| Dependency | Pinned version | Scope | License |
| --- | --- | --- | --- |
| BenchmarkDotNet, BenchmarkDotNet.Annotations | 0.15.8 | Benchmark executable only | MIT |
| CommandLineParser | 2.9.1 | Transitive benchmark tooling | MIT |
| Gee.External.Capstone | 2.3.0 | Transitive benchmark tooling | MIT |
| Iced | 1.21.0 | Transitive benchmark tooling | MIT |
| Microsoft.CodeAnalysis.Common, Microsoft.CodeAnalysis.CSharp | 4.14.0 | Transitive benchmark tooling | MIT |
| Microsoft.CodeAnalysis.Analyzers | 3.11.0 | Transitive benchmark tooling | MIT |
| Microsoft.Diagnostics.NETCore.Client | 0.2.510501 | Transitive benchmark tooling | MIT |
| Microsoft.Diagnostics.Runtime | 3.1.512801 | Transitive benchmark tooling | MIT |
| Microsoft.Diagnostics.Tracing.TraceEvent | 3.1.21 | Transitive benchmark tooling | MIT |
| Microsoft.DotNet.PlatformAbstractions | 3.1.6 | Transitive benchmark tooling | MIT |
| Microsoft.Extensions.DependencyInjection, DependencyInjection.Abstractions, Logging, Logging.Abstractions, Options, Primitives | 6.0.0 | Transitive benchmark tooling | MIT |
| Perfolizer | 0.6.1 | Transitive benchmark tooling | MIT |
| Pragmastat | 3.2.4 | Transitive benchmark tooling | MIT |
| System.CodeDom, System.Management | 9.0.5 | Transitive benchmark tooling | MIT |
| System.Reflection.TypeExtensions | 4.7.0 | Transitive benchmark tooling | MIT |
| CSharpier | 1.3.0 | Local formatting tool | MIT |
| Microsoft.CodeAnalysis.PublicApiAnalyzers | 5.6.0 | Build analyzer only; private assets | MIT |
| Microsoft.NET.ILLink.Tasks | 10.0.14 | SDK-selected trim/AOT build analysis; private assets | MIT |
| Microsoft.DotNet.ILCompiler, runtime-specific compiler and Microsoft.NETCore.App.Runtime.NativeAOT packs | 10.0.14 | Native test consumers and their SDK-selected compiler/runtime only | MIT; upstream third-party notices retained with native artifacts |
| Microsoft.CodeAnalysis.Analyzers, Microsoft.CodeAnalysis.CSharp | 5.9.0 | ZkLink analyzer build/tests; private assets | MIT |
| Microsoft.CodeAnalysis.Common | 5.9.0 | Transitive analyzer build/tests | MIT |
| NETStandard.Library | 2.0.3 | Analyzer target build only | MIT |
| Microsoft.NETCore.Platforms | 1.1.0 | Transitive analyzer target build only | MIT |
| System.Buffers, System.Numerics.Vectors | 4.6.1 | Transitive analyzer build/tests | MIT |
| System.Memory, System.Threading.Tasks.Extensions | 4.6.3 | Transitive analyzer build/tests | MIT |
| System.Collections.Immutable, System.Reflection.Metadata | 10.0.1 | Transitive analyzer build/tests | MIT |
| System.Runtime.CompilerServices.Unsafe | 6.1.2 | Transitive analyzer build/tests | MIT |
| System.Text.Encoding.CodePages | 8.0.0 | Transitive analyzer build/tests | MIT |
| Microsoft.Extensions.DependencyInjection.Abstractions | 10.0.14 | Dependency-injection integration only | MIT |
| Microsoft.Extensions.Hosting.Abstractions | 10.0.14 | Dependency-injection integration only | MIT |
| Microsoft.Extensions.Hosting | 10.0.14 | Dependency-injection tests only | MIT |
| Microsoft.Extensions.Configuration.Abstractions, Diagnostics.Abstractions, FileProviders.Abstractions, Logging.Abstractions, Options, Primitives | 10.0.14 | Transitive dependency-injection integration | MIT |
| Other Microsoft.Extensions hosting/configuration/logging packages, System.Diagnostics.DiagnosticSource, System.Diagnostics.EventLog, System.Text.Json | 10.0.14 | Transitive dependency-injection tests only | MIT |
| NSubstitute | 6.2.0 | Test only | BSD-3-Clause |
| Castle.Core | 5.1.1 | Transitive test only | Apache-2.0 |
| xunit.v3.mtp-v2, xunit.v3.assert, xunit.v3.common, xunit.v3.core.mtp-v2, xunit.v3.extensibility.core, xunit.v3.runner.common, xunit.v3.runner.inproc.console | 4.0.2 | Test only | Apache-2.0 |
| xunit.analyzers | 2.2.0 | Test analyzer | Apache-2.0 |
| Microsoft.Testing.Platform, Microsoft.Testing.Platform.MSBuild, Microsoft.Testing.Extensions.Telemetry, Microsoft.Testing.Extensions.TrxReport.Abstractions | 2.5.1 | Transitive test only | MIT |
| Microsoft.ApplicationInsights | 2.23.0 | Transitive test only | MIT |
| Microsoft.Bcl.AsyncInterfaces, System.Diagnostics.EventLog | 6.0.0 | Transitive test only | MIT |
| Microsoft.Win32.Registry | 5.0.0 | Transitive test only | MIT |
| System.Security.AccessControl | 6.0.1 | Transitive test only | MIT |
| Apache ZooKeeper Jute, Java client and server | 3.8.7, 3.9.6 | Java oracle and portable native-test ensembles only | Apache-2.0 |
| SLF4J API | 2.0.17 | Test-only Java logging API; no logging provider loaded | MIT |
| SLF4J API | 2.0.13 | Portable Java ensemble logging only | MIT |
| Logback classic/core | 1.3.15 | Portable Java ensemble logging only | EPL-1.0 or LGPL-2.1 |
| Netty buffer/codec/common/handler/resolver/transport/classes-epoll/native-unix-common | 4.1.137.Final | Portable Java ensemble TLS and transport only | Apache-2.0 |
| Apache Yetus audience annotations | 0.12.0 | Transitive portable Java ensemble dependency | Apache-2.0 |
| Apache Commons IO | 2.17.0 | Transitive portable Java ensemble dependency | Apache-2.0 |
| Dropwizard metrics-core | 4.1.12.1 | Portable Java server metrics only | Apache-2.0 |
| snappy-java | 1.1.10.5 | Portable Java server snapshot support only | Apache-2.0; bundled upstream notices retained in original jars |
| Gradle wrapper | 9.8.2 | Java reference build tool | Apache-2.0 |

Apache ZooKeeper is a project of the Apache Software Foundation. Its Jute schema and archive behavior define the wire format. The fixture utilities use published Jute and client classes; no ZooKeeper implementation source or jar is checked into this repository. Gradle downloads the jars into its dependency cache. The upstream source license and notice are preserved under `tests/ZkLink.JavaOracle/licenses/` at the same pinned commit; the Maven Jute jar itself has no license/notice entries. Fixture data is produced by the original utilities under `tests/ZkLink.JavaOracle/src/main/java/`.

Gradle wrapper scripts and their bootstrap jar are checked in using the standard generated wrapper, preserving their upstream license headers. Its distribution checksum is pinned. Exact Java dependencies are locked in `gradle.lockfile`, and jar/POM integrity hashes are in `gradle/verification-metadata.xml`. These tools and dependencies are not included in the managed driver.

The portable native-test fixture at `tests/ZkLink.JavaEnsemble` reuses this pinned
wrapper with its own two isolated runtime graphs, lock and verification metadata.
Its helper uses Apache public APIs and runs unmodified server jars; it copies no
server implementation source. Runtime jars stay in local test artifacts/cache,
with their upstream license/notice entries intact. CI retains runtime coordinates
and hashes, configuration and results rather than redistributing those jars.

Java tooling provenance, artifact hash, source commit, exact fixture semantics and reproduction commands are in [the fixture guide](tests/ZkLink.JavaOracle/README.md). Review upstream notices and final redistribution assets again before packaging. No Apache endorsement is implied.
