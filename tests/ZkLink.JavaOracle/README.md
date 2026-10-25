# Apache Java protocol reference

An oracle is an independent reference used by tests. This small Gradle project runs Apache ZooKeeper's actual serializers to generate expected bytes and decode bytes produced by C#. It checks compatibility independently of the C# encoder/decoder. Java is a test tool only: the managed driver and ordinary .NET tests require neither Java nor Gradle.

## Run the verification

From the repository root on macOS/Linux:

```sh
bash dev/test-jute-oracle.sh
```

Use the pinned .NET SDK and JDK 25 for the full verification command. Local verification and CI select Temurin 25.0.5+8. The checked-in Gradle 9.8.2 wrapper downloads its checksum-verified distribution; a global Gradle installation is unnecessary. Dependencies resolve from Maven Central with exact version locks and SHA-256 verification metadata. The Java code compiles for Java 17.

The script creates fresh Java/C# output directories under ignored `artifacts/jute-oracle/`, compares regenerated fixtures with the checked-in files, restores locked .NET dependencies, runs the protocol tests with C# byte output enabled, and asks Java to deserialize and assert those C# requests. Missing prerequisites, changed bytes, missing output files and mismatched values fail verification. Checked-in fixtures are never replaced automatically.

The project exposes Gradle tasks `generateFixtures`, `verifyFixtures` and `verifyCSharpRequests`; `check` also verifies Java-generated fixtures. For example:

```sh
bash tests/ZkLink.JavaOracle/gradlew -p tests/ZkLink.JavaOracle \
  --project-cache-dir artifacts/java-oracle-cache --no-daemon check
```

`-PoracleOutput=/absolute/path` selects the generated fixture directory; `-PcsharpOutput=/absolute/path` selects C# output to verify. The full script supplies fresh directories to prevent stale output from satisfying verification. Build output and the project cache stay under the repository's ignored `artifacts/` directory when using the commands above.

## Fixture contents

- `src/main/java/JuteOracle.java` checks signed integer/long boundaries, null/empty buffers and strings, BMP UTF-8, booleans, vectors, all Stat fields and headers. Its checked-in output is `../ZkLink.Protocol.Tests/Fixtures/jute-records.hex`.
- `src/main/java/ProtocolOracle.java` uses published Jute records plus the real `MultiOperationRecord` and `MultiResponse` classes. It covers 36 request/reply cases across 27 opcodes, with an ordinary error packet for each case, every create mode, null/empty data, new/resumed handshakes, notifications and mixed transaction results.
- `../ZkLink.Protocol.Tests/Fixtures/Protocol/manifest.json` records the source commit, opcode, xid and Java record classes for each case. The C# suite checks this manifest against its fixture catalog.
- `path-code-units.hex` records Apache PathUtils validity for all 65,536 UTF-16 code units inside a path. Additional C# tests cover relative components, sequential prefixes and chroot boundaries.

Java's string writer handles surrogate code units differently from strict UTF-8. String parity fixtures deliberately use ASCII and non-surrogate BMP characters; supplementary-character string interoperability is not established. Znode paths reject upstream-forbidden surrogate code units. Znode data remains arbitrary bytes.

These fixtures establish wire-format parity for the stated inputs. They do not establish live-server operation behavior, session recovery, authorization, watch restoration or support for any server patch.

## Allocation check

The protocol suite also records a small GetData codec allocation baseline, without a timing/throughput gate. The script writes `artifacts/jute-oracle/allocation-baseline.json` with workload, iteration count, runtime/OS/architecture, observed allocations and initial budgets. This is one codec workload, not a production performance qualification. The Java verification CI job retains that report.

## Provenance

Frozen on 2026-10-25:

| Item | Value |
| --- | --- |
| Apache artifacts | [zookeeper-jute:3.9.6](https://repo.maven.apache.org/maven2/org/apache/zookeeper/zookeeper-jute/3.9.6/), [zookeeper:3.9.6](https://repo.maven.apache.org/maven2/org/apache/zookeeper/zookeeper/3.9.6/) |
| Jute jar SHA-256 | `74f9cb1e4ba899ecc4b816201b4afd1c63c256705bd9176393fac4071597c75f` |
| Client jar SHA-256 | `09bdf93be7c0b5b7cd9fe51bee6abe424796cd5aaf75cf0c10bbd6f1273562ee` |
| Jar source commit | `a355171b081b5b60749db8f19cca1528b0df936f` |
| Schema | [zookeeper.jute](https://github.com/apache/zookeeper/blob/a355171b081b5b60749db8f19cca1528b0df936f/zookeeper-jute/src/main/resources/zookeeper.jute) |
| Reference behavior | [MultiOperationRecord](https://github.com/apache/zookeeper/blob/a355171b081b5b60749db8f19cca1528b0df936f/zookeeper-server/src/main/java/org/apache/zookeeper/MultiOperationRecord.java), [MultiResponse](https://github.com/apache/zookeeper/blob/a355171b081b5b60749db8f19cca1528b0df936f/zookeeper-server/src/main/java/org/apache/zookeeper/MultiResponse.java), [PathUtils](https://github.com/apache/zookeeper/blob/a355171b081b5b60749db8f19cca1528b0df936f/zookeeper-server/src/main/java/org/apache/zookeeper/common/PathUtils.java) |
| Gradle | 9.8.2; distribution SHA-256 `21bd5da2de49b642904077f38c0efdc7778e92b3ed0889392dca61b97e389607` |
| Wrapper jar SHA-256 | `3b8a25775a69158b5ad2b1d17a80a88dc7b40a352a73eb27d06c612a7ce68e98` |
| Java dependencies | `gradle.lockfile`; jar/POM hashes in `gradle/verification-metadata.xml` |
| Verified local runtime | Temurin OpenJDK 25.0.5+8, macOS arm64 |

The Apache jars are downloaded without modification. Upstream `LICENSE.txt` and `NOTICE.txt` from the pinned source commit are preserved in `licenses/`. Gradle wrapper scripts preserve their generated upstream headers. See the root third-party inventory before distributing test artifacts.
