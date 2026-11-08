# Run a seeded soak workload

Run the default two-minute workload on each pinned ZooKeeper version:

```sh
just zk-soak
```

Choose the duration in seconds per version and the workload seed:

```sh
just zk-soak 30 1730
```

The duration range is 30–86,400 seconds; the seed range is 0–2,147,483,647.
Release qualification requires six continuous hours on each version. Use the
supervised isolated command below for that run. Setup, the final round and
cleanup add bounded overhead. Use the SDK pinned in `global.json`, Docker with
Compose, and Python 3. Host Java and Gradle are unnecessary for this command.

For additional confidence, an optional longer run can use up to 24 hours per
version: `just zk-soak-isolated 86400 1729`. A completed longer run can satisfy the same
minimum; an interrupted run's checkpoints do not substitute for normal completion
and verified cleanup. Shortening the duration does not resolve retained failures.

Each version gets a fresh disposable, labeled three-voter fixture. All nodes
must report the exact version, pass authenticated create/read/delete probes,
and form one leader with two followers. The first observed follower is the
forwarding target. The workload closes only its own forwarding connection;
server processes and network interfaces remain running.

## Isolated Linux execution

Run the same checks with proxy listeners confined to an owned Linux network
namespace:

```sh
just zk-soak-isolated 120 1729
ZKLINK_MATRIX_ARTIFACT_ROOT="$PWD/artifacts/soak" just zk-soak-isolated 21600 1729
```

This command builds with the pinned host SDK, then executes the test DLL in a
runtime-only .NET image pinned by digest in `dev/dotnet-soak-runtime.env`. The
container runs as the invoking nonroot user, mounts the compiled tests read-only,
and writes to the owned evidence directory. It shares the selected follower's
network namespace and publishes no additional ports. Maintenance clients connect
through a fixture-owned loopback byte relay to the observed leader's private
address. This keeps the core's literal-loopback plaintext-digest guard intact
while preserving independent leader write fences. The relay remains available
through namespace cleanup and is disposed with the maintenance client; it is
outside the per-round fault-proxy socket scope. The runtime has no Docker socket
or privileged access.
Host loopback traffic cannot connect directly to these proxy listeners.

The supervisor records source and binary hashes, runtime platform/image,
namespace identity, seed, deadlines and cleanup receipts. It observes the driver
container, all three server containers, host free space and VM storage pressure,
disk, memory and I/O counters. These observations include fixture and shared-VM
costs; they are not performance measurements. The runtime resource journal still
requires zero owned counters, live proxy connections and listeners.

Execution stops on source or binary changes, runtime failure, less than 5 GiB
host or 2 GiB VM disk space, or an observation gap exceeding 120 seconds in UTC
or monotonic time. The command wall-time guard allows the requested duration per version plus
ten minutes of overhead. Failure cleanup has a separate grace period. The driver has a separate guard of the requested duration plus 150 seconds. On macOS an owned `caffeinate` process prevents idle sleep; forced
sleep or a closed lid can still interrupt continuity. No power or network settings
are changed. Runs of at least six hours first require a fresh 30-second check on
each selected version using the same source inputs and runtime pin.

Interrupt with Ctrl-C or send SIGTERM to the supervisor PID recorded in `status.json`. It gives the
owned runner up to three minutes for authenticated all-node rollback and teardown,
then forcibly stops a stuck runner and attempts owned emergency teardown. Emergency
teardown is a failed cleanup result and does not establish authenticated rollback.
Evidence remains in the printed directory, including `status.json`, `run.json`,
`resources.jsonl`, the runner log and each version's driver/resource/cleanup files.
A successful short run is not release qualification; the supervisor always leaves
`releaseQualified` false pending candidate and failure-disposition review.
Failed or interrupted runs are retained and never resumed automatically.

For a focused diagnostic, use
`python3 dev/supervise-zookeeper-soak.py --seconds 30 --server-version 3.9.6`.
The normal command always checks both versions. The isolated process evidence
applies to its recorded Linux architecture; it does not establish native macOS
or Windows process behavior or explain the historical unexpected peer traffic.

## Workload and recovery

The seed selects two to eight jobs per round, payloads of 32–256 bytes and
read-before-write choices. Jobs run in batches of at most four concurrent
requests. Each job creates, updates, reads and deletes a protected node, checking
the observed data. The seed reproduces these workload choices; operating-system
and network scheduling remain variable.

Each round creates and disposes a client, authenticates, registers one-shot and
persistent watches, and creates an ephemeral. Cancellation checks cover:

- A precancelled create returning `NotSent`, with no server node.
- A successful sequential create whose reply is withheld. Independent control
  first completes a quorum write with a transaction ID greater than the captured
  create reply, then verifies exactly one committed child; cancellation must
  report `Unknown`. A sync acknowledgment alone is insufficient for this
  cross-session freshness check.

The source forwarding endpoint then closes. A second endpoint holds a real
watch-reset acknowledgment during same-session recovery. Readiness must remain
pending and a new request must fail `NotSent` while restoration is held. After
release, the checks require retained session/ephemeral ownership, authentication,
a reconnect gap, actual one-shot and persistent invalidations, protected reads,
and no create replay through the recovery endpoint. Acknowledged close must
remove the ephemeral. Independent maintenance uses the observed leader and
acknowledged write fences before checking deletion and before taking the recursive
cleanup snapshot. This also drains the leader's predicted child counts after
session close. A follower-applied fence alone is insufficient for that server
bookkeeping. The fixture records the maintenance role and rejects a changed leader
at its final role check; the client under test still uses the forwarding follower.
Cleanup errors remain failures, with no automatic delete retry.
Initial connection attempts may retry before readiness. Once each forwarding
endpoint reaches its readiness checkpoint, unexpected additional attempts fail
the round.

Each version also runs a controlled deadline check. A proxy holds one successful
`Sync` reply beyond the unchanged five-second operation deadline. The caller
must receive `ZooKeeperTimeoutException` with `Unknown`; releasing the frame must
allow another sync on the same session and generation, with no replay and an
acknowledged close. Owned counters and sockets must return to zero. This checks
late-reply handling; it does not reproduce server disk latency.

## Resource checkpoints

Every round must return active watches, event backlog, application requests,
owned request bytes, tombstones and disconnected-session counters to zero.
A passive listener retains only the six latest counter values.

The harness registers each original listener and every accepted or connected
upstream socket while it is live. After disposal, all registered socket handles
must be closed, and scoped TCP snapshots must show no active connection matching
an owned address/port pair in either direction. Pending handshakes to the exact
listener endpoints are also checked. The inventory has finite bounds and cannot
silently drop registrations.

A port number alone does not establish ownership: another loopback address or a
later socket can use it. In a shared Linux namespace, Docker's DNS listener can
have the same port number as a proxy on a different loopback address. The checks
retain the original listener handle and compare exact connection endpoints,
including equivalent IPv4-mapped addresses. Retained listeners and `CLOSE_WAIT`
peers still fail the unchanged ten-second guard. Kernel `TIME_WAIT` is excluded;
namespace-wide totals are not treated as a per-process leak measure.

A socket-cleanup timeout retains a bounded snapshot of matching TCP/listener
states, numeric address families and ports, and open owned-handle counts. On
Linux, best-effort kernel inode/UID and test-process descriptor membership add
ownership evidence. Missing or unsupported ownership lookup is explicit and
never replaces the original failure. Addresses, payloads, arguments and
environments are excluded from this snapshot.

After five initial rounds, the harness records a resource baseline. Subsequent
quiescent samples record:

- A retained managed-heap estimate using [`GC.GetTotalMemory(true)`](https://learn.microsoft.com/en-us/dotnet/api/system.gc.gettotalmemory?view=net-10.0).
- Process working set and native thread count.
- Completed rounds, CRUD request totals, cancellation outcomes, reconnects and
  delivered watch-event totals.

The run fails if the retained-heap estimate exceeds baseline by 16 MiB or native
threads exceed baseline by 32. Working set is descriptive evidence. Collection
and sampling affect execution, so these results do not establish throughput or
latency. The heap/thread observations include the harness and independent fixture
client as well as the driver.

Samples are streamed to JSONL at intervals of at least ten seconds, increasing
for longer runs to bound periodic samples at 1,440. The harness retains aggregate
state instead of an activity history. A failure record contains the safe error
type, raw server error code, operation and request outcome when available, seed
and counters. Samples and failures include UTC reporting times; failures also
include elapsed workload time. Round failures are flushed before owned cleanup;
a distinct cleanup failure adds a second record without replacing the primary
one. Propagating the same exception does not duplicate its record. Guarded waits
include a fixed `wait_site` label. A rejected proxy prefix adds `frame_failure`
with the original UTC observation time, direction, handshake state, connection
attempt, frame number, rejected length and configured maximum. Its observation
time can precede cleanup reporting; reporting order alone does not establish
which error happened first. Journals exclude exception messages, credentials,
paths and payloads.

Failures also retain a bounded connection timeline with fixed event names,
timestamps, numeric socket errors and attempt counts. The timeline holds at most
128 events and reports dropped-event counts; it contains no wire bytes or
endpoint values.

A cleanup delete rejected with `NotEmpty` retains the original rejection and a
bounded diagnostic snapshot. It records traversal depth, child counts, child
version/transaction metadata and counts of fixed fixture-node categories before
and, when a follow-up read succeeds, after the rejection. Names remain excluded.
The harness does not retry the delete to turn the failed round into a pass.

## Bounds, interruption and evidence

Rounds have a thirty-second deadline; readiness, resource and socket checks
allow ten seconds. Requests, connect, close and independent cleanup also have
finite deadlines. The process guard is the requested duration plus two minutes.
The test itself has a fixed maximum guard beyond the longest permitted duration.

Managed cleanup disposes watches, clients and proxies and observes outstanding
work. Before reading the round namespace for deletion, the independent cleanup
session performs a write on its fixture-owned parent, establishing its view
through the quorum. It then removes the round namespace; unexpected cleanup
errors remain failures. The shell exit trap runs fresh authenticated
probes on all three members, then removes the owned fixture and volumes even
when the test process fails. The printed artifact directory retains exact server
versions, configuration, SDK/revision, seed, test results, the JSONL resource
journal, native logs and cleanup results.

CI keeps the native two-minute selection and separately checks 30-second isolated
workloads, driver termination and supervisor interruption on both versions. Longer
runs use the same workload checks. A short passing run provides bounded soak
evidence; the six-hour minimum on each version needs its own completed results.

## Observed local runs

On macOS arm64 with .NET 10, the 120-second runs with seed 1729 passed on
ZooKeeper 3.8.7 and 3.9.6 on 28 October 2026. They completed 1,234 and 1,223
rounds respectively, with 27,834 and 27,552 successful CRUD requests. The
30-second runs with seed 1730 also passed on both versions. Every resource
checkpoint had zero owned counters, live proxy connections and listeners.

Separate checks confirmed that the observers detect a retained 32 MiB allocation
and an open owned TCP connection. Deliberate managed-exception and abrupt-process-exit
runs failed as expected, retained their settings and verified fresh authenticated
readiness on all three members before fixture removal. These checks provide
bounded local evidence; they do not establish the six-hour release minimum.

On 29 October, the release-duration attempt failed after about two hours on
3.8.7 with a `DeleteAsync` timeout. All three hourly cleanup tasks began within
120 ms of each other just before server disk flush stalls of up to 6.7 seconds.
The historical journal lacks the UTC request observation needed to establish
causality. Fixture rollback and removal completed.

The original notes called this a sync timeout because the stack names
`SyncCoreAsync`. That private reply helper also serves deletes; the retained
call site identifies `DeleteAsync`. Use request operation metadata and the
matching source line when classifying a timeout. Controlled-peer checks now
verify delete-specific `Unknown` metadata, retained in-flight capacity, late
reply draining and no replay. These checks do not establish the historical
timeout's cause.

Subsequent five-minute seed-1729 workloads passed on both versions. A separate
90-second workload per version also passed while six 64 MiB scratch files on
each owned server volume were written and deleted concurrently. The controlled
late-sync check passed on both patches. These bounded checks did not reproduce
the historical timeout or qualify the release duration; its cause remains open.
A deliberate primary-error-plus-cleanup-error probe verified ordered, redacted
failure records and authenticated all-node rollback.

For a timeout near maintenance activity, retain the UTC failure and native
server timeline along with container block I/O, VM I/O pressure, memory and disk
counters. Preserve the failed result and unchanged deadlines when repeating the
hourly boundary; another short passing run does not resolve it.

[How-to guides](README.md) · [Documentation home](../README.md)
