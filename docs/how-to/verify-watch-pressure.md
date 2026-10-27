# Verify slow watch consumers and buffer limits

Run:

```sh
just zk-watch-pressure-faults
```

Use the SDK pinned in `global.json`, Docker with Compose, and Python 3. Host
Java and Gradle are unnecessary; readiness probes use the JDK in the pinned
server images.

The command creates a disposable three-voter fixture for each of ZooKeeper
3.8.7 and 3.9.6. Every node must report the exact version and pass authenticated
create/read/delete probes; the fixture must have one leader and two followers.
The first observed follower is a transparent forwarding target. The test changes
only its own clients and protected namespaces. It leaves server processes and
network interfaces running.

## Slow consumers and disposal

Each owner permits three watch handles: two persistent handles sharing one child
path and one recursive handle at its parent. The distinct paths respect the
3.8 compatibility profile. A synchronized data change, deletion and recreation
produce three distinguishable invalidations for each handle.

With `MaxBufferedEvents=12`, all nine entries remain unread. While those queues
are held, the test requires two actual forwarded heartbeats and two valid
heartbeat acknowledgments observed through round-trip telemetry. A subsequent
protected read must succeed with the same session, connection generation and
single proxy connection.

Three rounds use distinct paths on the same owner with `MaxWatches=3`. Full
handle capacity must be reusable. Normal draining checks exact event kind, path,
logical registration and connection generation before confirmed removal. The
middle round explicitly disposes an unread shared handle: its three entries
and one active handle must be released, its stream must complete, and the other
two streams must retain their buffered events in order. The next round must
register all three handles again.

Active watches, buffered events, application requests, owned request bytes and
tombstones must return to zero between rounds and after disposal. The isolated
test collection keeps other managed clients quiescent during these aggregate
counter observations.

These checks apply to the synchronized invalidations in this workload. Watch
events still require rereading application state.

## Explicit overflow

A separate owner uses `MaxBufferedEvents=10`. An unread initial state snapshot
and nine watch entries fill this shared budget exactly. Ordinary work must still
succeed at that boundary. Creating a sibling then matches only the recursive
handle and requires one additional entry. That single entry must fault the owner
with `ZooKeeperEventOverflowException`, proving that the unread state snapshot
participates in the same limit as watch entries.

The test requires:

- Observable terminal `Faulted`, failed readiness and `NotSent` for a new request.
- Each watch's original three entries in order, followed by the typed overflow.
- The buffered initial state followed by the same explicit terminal error.
- One overflow metric, unchanged owner identity/generation and no replacement connection.
- A fresh independent control write/read after the owner faults.
- Released watch/event/request counters after draining and disposal.

Applications recover from overflow by explicitly creating a new client and taking
a fresh snapshot. The test never silently replaces the faulted owner.

## Bounds and cleanup

Each case has a sixty-second guard. Counter/state waits allow ten seconds;
heartbeat observation allows twelve seconds. Forwarding endpoints have a
seventy-five-second lifetime and the test process a two-minute guard. Setup,
requests, close and namespace cleanup also have finite deadlines.

Managed cleanup disposes watches, clients and forwarding sockets and removes
the namespace through an independent authenticated client. A shell exit trap
performs fresh authenticated probes through every server before removing the
owned fixture and volumes. Deliberate exception and test-process-exit probes
exercise cleanup with unread queues; their expected failures are recorded
separately from passing cases.

The printed artifact directory retains versions, roles, configuration, safe
counter/heartbeat observations, test output, server logs and cleanup results.
Managed diagnostics exclude credentials, session passwords, paths and payloads.
The fixture uses plaintext with the explicit development digest override.

Local checks cover both versions with a macOS arm64 client. The
[CI workflow](../../.github/workflows/build.yml) selects pressure tests separately
from operation, server-outage and network-partition cases; remote execution
remains unobserved. These bounded rounds establish resource-counter and capacity
behavior. Long-running soak, retained heap/RSS, thread/socket growth, secure
pressure tests and broader load qualification require separate evidence.

[How-to guides](README.md) · [Documentation home](../README.md)
