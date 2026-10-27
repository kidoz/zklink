# Verify recovery after a follower stops

Run the dedicated fault check with:

```sh
just zk-follower-faults
```

Use the SDK pinned in `global.json`, Docker with Compose, and Python 3. The
runner uses the JDK inside each pinned server image for readiness and role checks;
host Java and Gradle are unnecessary for this check.

The follower and [leader checks](verify-leader-recovery.md) share a role-validated
runner and outage scenario. The command creates separate disposable three-node
ensembles for ZooKeeper 3.8.7 and 3.9.6. Published ports bind to loopback. Every node must report the
expected version and pass an authenticated create/read/delete probe. The runner
uses [`zkServer.sh status`](https://zookeeper.apache.org/doc/r3.9.6/zookeeperTools.html)
to require one leader and two followers, then selects the first observed follower
in `zoo1`, `zoo2`, `zoo3` order.

## What the test does

The managed client connects through a forwarding endpoint targeting that
follower. It creates an ephemeral node and registers one-shot and persistent
watches on distinct protected paths. Future endpoint choices are updated to the
other two servers through separate forwarding endpoints.

To make uncertainty repeatable, the follower endpoint withholds one successful
sequential-create response while leaving the TCP connection open. A separate
authenticated control client verifies the committed child. The test then stops
the actual follower container. Upstream closure must follow that stop; the create
call must return `Unknown` from connection loss, rather than an operation timeout.

During recovery, the surviving endpoint holds an actual watch-reset acknowledgment.
The client must remain `Restoring`, reject a new request as `NotSent`, and keep
readiness and the persistent reconnect gap pending. After release, the checks
require:

- The same session ID, a later connection generation and the retained ephemeral.
- One reconnect gap followed by a real persistent-watch invalidation.
- One-shot delivery and completion after a change during restoration.
- Successful protected reads after authentication replay.
- Exactly one sequential child and no create transmission through either recovery
  endpoint, proving the uncertain call was not replayed.
- Acknowledged close and observed ephemeral deletion.

Control-client `Sync` calls precede cross-client observations of the committed
child and close cleanup. The forwarding endpoints control response visibility;
they do not fabricate server replies or replace the real server-stop fault.

## Bounds and cleanup

Only one container can be stopped. The test validates the generated project name,
run ID, target service and matching container labels before control actions, then
checks the target is still a follower. A mismatch aborts before stopping it.

The test has a 90-second guard, forwarding endpoints have 75-second lifetimes,
and the runner has a two-minute test-process guard. Recovery/event waits and Docker
actions are bounded. The two surviving servers must continue accepting writes.

A `finally` block releases held acknowledgments, restarts the selected follower
and requires follower role plus authenticated readiness. A separate shell exit
trap restores and checks the owned ensemble if the test process fails or exits.
The runner removes only its own containers and volumes. Selected tests fail on
missing configuration instead of skipping.

The printed evidence directory retains exact versions, observed roles, selected
service, Compose configuration, test output, safe telemetry, fault timings,
rollback results and server logs. Request buffers are bounded and cleared; the
driver telemetry excludes credentials, session passwords, paths and payloads.

Local checks passed on both versions with a macOS arm64 client. A deliberate
test abort after stopping the follower additionally verified restoration and
cleanup; that expected failure is recorded separately from passing checks.
The [CI workflow](../../.github/workflows/build.yml) defines separate follower and
leader fault jobs; remote execution remains unobserved.

This check uses plaintext client connections with the explicit development
digest override. Leader recovery is checked separately by `just zk-leader-faults`.
[Quorum loss](verify-quorum-recovery.md) also has a separate command.
[Incoming client-traffic loss](verify-partition-recovery.md) has its own check;
other partition directions, forced expiry, observer endpoints, secure server outages and sustained load
require separate evidence.

[How-to guides](README.md) · [Documentation home](../README.md)
