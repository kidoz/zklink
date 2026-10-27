# Verify recovery after a leader stops

Run the dedicated leader check with:

```sh
just zk-leader-faults
```

Use the SDK pinned in `global.json`, Docker with Compose and Python 3. The runner
uses Java tools inside the pinned server images; host Java and Gradle are not
required for this check.

The leader and [follower checks](verify-follower-recovery.md) share the same
labeled disposable fixtures, response controls and managed outage scenario.
Each command runs ZooKeeper 3.8.7 and 3.9.6 separately with loopback ports and a
unique authenticated namespace. Both roles have separate CI matrix jobs.

## Election and recovery assertions

Before the fault, all three exact-version servers must pass authenticated
readiness and report one leader and two followers. The client connects through
the observed leader. The controller rechecks its role and ownership labels
immediately before stopping that one container.

The test requires the two survivors to report one replacement leader and one
follower within a fifteen-second election guard. The replacement must be a
different service from the stopped member. A protected write after election
also verifies that the remaining quorum can make progress.

The shared scenario checks:

- An uncertain committed sequential create returns `Unknown` after actual
  upstream loss and is never replayed through recovery endpoints.
- Recovery preserves the valid session ID and ephemeral owner, with a later
  connection generation.
- Authentication and actual watch-reset acknowledgment precede readiness;
  requests are `NotSent` and no reconnect gap is published while that
  acknowledgment is held.
- One-shot invalidation, a persistent reconnect gap and subsequent real watch
  delivery work after reconnection.
- Protected reads and acknowledged close succeed, followed by observed
  ephemeral deletion.

One successful create reply is withheld before the stop to make uncertainty
repeatable. The connection remains open until the real server stops. Recovery
proxies keep accepting the driver's attempts when an upstream client port is
temporarily unavailable during election; they do not retry application requests.
The [follower guide](verify-follower-recovery.md) describes the common wire
controls, observation barriers and safe telemetry in more detail.

## Restore and retain evidence

Before restarting the old leader, rollback waits for a survivor leader. The old
member must return as a follower and pass authenticated readiness. The test then
checks that the previously observed replacement is still leader. Rollback uses
an independent fifty-second guard; test, proxy, Docker-action and process guards
remain bounded. A shell exit trap provides a second restoration and teardown
path if the test process fails.

The printed artifact directory contains versions, initial/final roles, selected
service, configuration, safe telemetry, event timings, rollback results and server
logs. The test records the replacement's service name without printing session
credentials, paths or payloads. A deliberate abort after replacement election
checks rollback separately from successful recovery.

Local checks cover both pinned versions with a macOS arm64 client. Remote CI,
quorum loss, partitions, forced expiry, observer endpoints, secure outages and
sustained-load behavior require separate evidence. The fixture uses plaintext
with the explicit development digest override. A valid session is preserved in
this bounded test; it does not promise indefinite preservation during an outage.

Role checks use [`zkServer.sh status`](https://zookeeper.apache.org/doc/r3.9.6/zookeeperTools.html).
The [Apache internals guide](https://zookeeper.apache.org/doc/r3.9.6/zookeeperInternals.html)
describes leader activation and quorum behavior.

[How-to guides](README.md) · [Documentation home](../README.md)
