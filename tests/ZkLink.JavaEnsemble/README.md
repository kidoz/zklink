# Portable ZooKeeper test ensembles

This Gradle project provides test-only ZooKeeper **3.8.7** and **3.9.6** runtimes
for native client verification on Windows, macOS and Linux. It uses unmodified
Apache server jars and a small authenticated readiness helper compiled against
the older API. Java is not a driver runtime or ordinary .NET build dependency.

Run from the repository root with .NET's native prerequisites, Python 3.12 or
newer and **Temurin JDK 25.0.5+8**:

```sh
python dev/test-portable-aot.py
```

Use `python3` on macOS/Linux if needed. Docker, Bash and the OpenSSL CLI are not
required. The runner publishes the native consumers, invokes the existing pinned
Gradle wrapper JAR, verifies separate dependency graphs and starts owned loopback
JVMs. Each fixture has three voters and an expired-certificate standalone control.
JDK `keytool` creates disposable PKCS12 stores and certificates outside retained
artifacts. Netty uses Java NIO/JSSE; optional native Netty transports are disabled.

`gradle.lockfile` pins dependencies, and `gradle/verification-metadata.xml` checks
jar/POM SHA-256 values. The export task also provides coordinates for every jar;
the runner checks the copied bytes against those hashes and detects runtime
changes. Both server graphs remain isolated, so selecting 3.8.7 cannot silently
resolve the newer ZooKeeper jar. No global Gradle installation is needed: the
wrapper distribution and its checksum belong to the existing Java reference tool.

To inspect/export the runtimes independently, supply a fresh output directory:

```sh
java -cp tests/ZkLink.JavaOracle/gradle/wrapper/gradle-wrapper.jar org.gradle.wrapper.GradleWrapperMain -p tests/ZkLink.JavaEnsemble --no-daemon --warning-mode fail --dependency-verification strict -PfixtureOutput=/absolute/fresh/directory exportRuntime
```

Intentional dependency updates must regenerate and review both the lock and
verification metadata with Gradle's `--write-locks --write-verification-metadata sha256`,
then repeat ordinary strict verification. Do not regenerate hashes to accept an
unexpected mismatch.

Server jars stay in ignored test output/cache with original license and notice
entries intact. CI retains runtime hashes/coordinates and public verification
evidence rather than the jars or private PKI. See the
[native verification guide](../../docs/how-to/verify-native-aot.md) for operations,
cleanup, evidence and platform qualification limits.
