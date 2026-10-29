# Run a seeded soak workload

Run the default two-minute workload on each pinned ZooKeeper version:

```sh
just zk-soak
```

Choose the duration in seconds per version and the workload seed:

```sh
just zk-soak 30 1730
just zk-soak 86400 1729
```

The duration range is 30–86,400 seconds; the seed range is 0–2,147,483,647.
The longer command requests 24 hours on each version. Setup, the final round and
cleanup add bounded overhead. Use the SDK pinned in `global.json`, Docker with
Compose, and Python 3. Host Java and Gradle are unnecessary for this command.

Each version gets a fresh disposable, labeled three-voter fixture. All nodes
must report the exact version, pass authenticated create/read/delete probes,
and form one leader with two followers. The first observed follower is the
forwarding target. The workload closes only its own forwarding connection;
server processes and network interfaces remain running.

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
  verifies one committed child; cancellation must report `Unknown`.

The source forwarding endpoint then closes. A second endpoint holds a real
watch-reset acknowledgment during same-session recovery. Readiness must remain
pending and a new request must fail `NotSent` while restoration is held. After
release, the checks require retained session/ephemeral ownership, authentication,
a reconnect gap, actual one-shot and persistent invalidations, protected reads,
and no create replay through the recovery endpoint. Acknowledged close must
remove the ephemeral. Independent control deletes the round's namespace.

## Resource checkpoints

Every round must return active watches, event backlog, application requests,
owned request bytes, tombstones and disconnected-session counters to zero.
A passive listener retains only the six latest counter values.

The harness captures its proxy listener and upstream local ports while they are
live. After disposal, scoped loopback TCP snapshots must show no remaining owned
listeners or live connections. Upstream matches also require the known server
port. Kernel `TIME_WAIT` is excluded; host-wide connection totals are not used
as a per-process leak measure. See the [.NET TCP snapshot documentation](https://learn.microsoft.com/en-us/dotnet/api/system.net.networkinformation.ipglobalproperties.getactivetcpconnections?view=net-10.0).

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
type, raw server error code and request outcome when available, seed and counters.
Managed output excludes credentials, paths and payloads.

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

The CI selection uses the default two-minute run on both versions. Longer runs
use the same command and checks. A short passing run provides bounded soak
evidence; the 24-hour release requirement needs its own executed results.

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
bounded local evidence; a 24-hour run has not been executed.

[How-to guides](README.md) · [Documentation home](../README.md)
