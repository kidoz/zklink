# Verify server-confirmed session expiry

Run the dedicated live check with:

```sh
just zk-session-expiry
```

Use the pinned .NET SDK, JDK 25.0.5, Docker with Compose and Python 3. The
repository's Gradle wrapper builds the Java management helper with locked
dependencies and strict checksum verification.

The command creates separate, disposable three-voter ensembles for ZooKeeper
3.8.7 and 3.9.6. It verifies exact versions, one leader and two followers, and
authenticated create/read/delete readiness on every member. The tested client
connects through a transparent proxy to an observed follower; independent
maintenance uses the observed leader and acknowledged write fences.

The client creates an ephemeral node and registers one-shot and recursive
watches on separate paths. The proxy withholds a successful sequential-create
reply after the server commits it. A container-local Java helper then invokes
ZooKeeper's [session termination operation](https://github.com/apache/zookeeper/blob/a355171b081b5b60749db8f19cca1528b0df936f/zookeeper-server/src/main/java/org/apache/zookeeper/server/ConnectionBean.java)
for that exact client session. The helper
matches the session ID without reading its password. Management ports are not
published to the host, and container control requires the runner's ownership
labels. An unknown-session negative control must fail while the valid client
remains usable.

The assertions require:

- Server-confirmed terminal `Expired`, with no local `Lost` classification.
- Typed expiry failures from both watch streams and connection readiness.
- Removal of the expired session's ephemeral node.
- `Unknown` for the committed create whose reply was withheld, with exactly one
  committed child and no automatic replay.
- No replacement session, further connection attempt or ephemeral recreation
  during three control checks spaced one second apart.
- Rejection of later owner requests as `NotSent`, and `SessionExpired` close status.

The test retains the existing client deadlines. Fixed process, event and test
guards bound the experiment. Missing fixture metadata fails before a client is
opened; it does not skip the test.

Evidence includes Gradle and SDK versions, resolved configuration, member
versions and roles, detailed test output, server logs and cleanup results. The
runner performs fresh authenticated probes on every member before teardown,
including after test failure, then removes its owned containers and volumes.

Local macOS arm64 and [hosted Linux checks](https://github.com/kidoz/zklink/actions/runs/41282372618)
pass on both pinned server versions. This verifies
explicit server management termination. Continuous soak qualification, natural
expiry timing under partitions and other native consumers need their own evidence.

[How-to guides](README.md) · [Documentation home](../README.md)
