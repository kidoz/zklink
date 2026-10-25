# Jute primitive fixture oracle

`JuteOracle.java` is an original test utility that calls Apache's serializer/deserializer. Java is required only for this optional verification command, not for building/running the driver or ordinary .NET tests.

From the repository root on macOS/Linux, run:

```sh
bash dev/test-jute-oracle.sh
```

Requires JDK 17+, curl, Python 3 and the pinned .NET SDK. The command downloads the test-only jar, verifies its SHA-256 and build commit, compiles the utility under ignored `artifacts/jute-oracle/`, regenerates Java bytes and compares them with the checked-in fixture, runs .NET tests with C# byte output enabled, then asks Java to decode and assert those C# bytes. Missing prerequisites, changed bytes, and mismatched values fail the command. It never replaces the checked-in fixture automatically.

The fixture is `tests/ZkLink.Protocol.Tests/Fixtures/jute-records.hex`:

- `int`: minimum, -1, 0, 1, maximum; then `long`: the same signed boundary values.
- Booleans false and true; strings null, empty, `ZooKeeper`, `Ж中é`.
- Buffers null, empty, and hex `00ff80`.
- A string vector containing the same four strings; then a null vector.
- Stat in schema order: `(-9223372036854775808, 9223372036854775807, -1, 0, -2147483648, -1, 2147483647, 72623859790382856, 123, 456, -2)`.
- Request header `(1, 4)`; reply header `(-2, 9223372036854775807, -101)`.

The record fixture checks primitive and record encoding; it does not cover opcode dispatch/special-xid routing, multi, handshake or live-server interoperability. Strict UTF-8 rejection is additionally tested in C#. Java's writer handles surrogate code units differently; this fixture deliberately covers non-surrogate BMP strings, and makes no supplementary-character interoperability claim.

Provenance frozen on 2026-10-25:

| Item | Value |
| --- | --- |
| Artifact | [org.apache.zookeeper:zookeeper-jute:3.9.6](https://repo.maven.apache.org/maven2/org/apache/zookeeper/zookeeper-jute/3.9.6/) |
| SHA-256 | `74f9cb1e4ba899ecc4b816201b4afd1c63c256705bd9176393fac4071597c75f` |
| Jar `Implementation-Build` | `a355171b081b5b60749db8f19cca1528b0df936f` |
| Schema | [zookeeper.jute at the same commit](https://github.com/apache/zookeeper/blob/a355171b081b5b60749db8f19cca1528b0df936f/zookeeper-jute/src/main/resources/zookeeper.jute) |
| Serialization | [BinaryOutputArchive.java](https://github.com/apache/zookeeper/blob/a355171b081b5b60749db8f19cca1528b0df936f/zookeeper-jute/src/main/java/org/apache/jute/BinaryOutputArchive.java), [BinaryInputArchive.java](https://github.com/apache/zookeeper/blob/a355171b081b5b60749db8f19cca1528b0df936f/zookeeper-jute/src/main/java/org/apache/jute/BinaryInputArchive.java) |
| Local generator runtime | Temurin OpenJDK 25.0.5+8, macOS arm64 |

The Apache jar is downloaded without modification; this Maven jar has no license/notice entries. The upstream `LICENSE.txt` and `NOTICE.txt` from the pinned commit are preserved in `licenses/`. It is not a driver dependency. See the root third-party inventory before distributing any test artifacts.
