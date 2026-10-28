# Measure recovery of distinct watched paths

Run one controlled reconnect with 10,000 persistent watches on distinct paths,
against fresh ZooKeeper 3.8.7 and 3.9.6 fixtures:

```sh
just bench-recovery
```

Use the smaller 100-path workload for a bounded smoke check:

```sh
just bench-recovery 100
```

The supported counts are 100 and 10,000. Run on Linux or macOS with the pinned
.NET SDK, Docker with Compose and Python 3. The shared
[live benchmark runner](run-live-benchmarks.md) owns isolated three-voter fixtures,
checks exact native versions and authenticated readiness, targets the observed
leader and retains configuration, machine, SDK, Docker and cleanup evidence.
Each version runs in a fresh managed process. CI selects 100 paths; timings are
informational.

## Workload and recovery checks

An independent authenticated control client creates a protected namespace and
one node per watch. The owner connects through an owned inspecting loopback proxy,
creates a session ephemeral and registers one persistent watch on every path.
Padded distinct paths make the full workload span multiple bounded restore frames.
The proxy counts native add-watch requests and distinct paths in restore vectors.

The owner has a watch limit equal to the workload count and a shared event budget
of twice that count: 20,000 events for the full run. This explicitly sized bounded
budget accommodates reconnect gaps and subsequent validation. There are no state
subscriptions or changes while disconnected.

The proxy closes only the owner's first connection and pauses the next connection
before its upstream handshake. The client may already show `Connecting` when the
proxy observes that retry; the report labels this a paused reconnect observation,
rather than the exact instant of the `Disconnected` transition. Readiness must
remain pending and an ordinary sync request must fail `NotSent`.

After resuming the connection, the proxy briefly holds the first real successful
watch-reset acknowledgment. The owner must show `Restoring`; readiness and all
gap reads must remain pending, and ordinary admission must still fail `NotSent`.
No sync probe may be transmitted through the proxy before recovery completes.
The hold is released immediately after these checks.

The checks then require:

- The same owner session, exactly one generation advance and a retained ephemeral.
- Two handshakes and two authentication requests, covering initial authentication
  and replay; no additional connection attempt.
- Native restoration of every distinct path, with acknowledged bounded frames and
  multiple frames for 10,000 paths.
- Exactly one reconnect gap per logical handle, with matching identity, path and
  generation and no fabricated server zxid.
- A later mutation, matching invalidation and protected read on **every** path,
  in batches of at most 32.
- No unexpected extra gap or event, stable owner/control sessions, acknowledged
  close, terminal stream completion and server-observed ephemeral deletion.
- Zero owned watch/event/request counters and independent namespace removal.

The experiment checks restoration and resnapshot readiness. A gap still requires
applications to reread state; it does not reconstruct changes missed during an
arbitrary disconnection. This workload does not exercise lost application replies,
server failures, TLS or automatic replacement sessions.

## Timing and resource definitions

Each case is one recovery observation. Reports separate:

- Cut to the proxy's next paused connection observation.
- The deliberate pause before the handshake and the observed reconnect handshake.
- Raw readiness time starting when the paused connection is resumed.
- The measured first-acknowledgment hold duration.
- Active readiness and restore-acknowledgment forwarding times, subtracting that
  hold; the pre-handshake pause is outside these active intervals.
- Ack release to receipt of every gap, and the remaining gap-consumption tail
  after observed readiness.

Restore wire timing ends when the proxy starts forwarding the final successful
reset acknowledgment. Readiness timing ends when the owner readiness waiter
resumes. Both include proxy inspection and managed scheduling; these are distinct
observations of the recovery path. Gap histogram CSV and p50/p95/p99 ranges use
ack release as their origin. Their precision follows the
[live histogram definitions](run-live-benchmarks.md#interpret-timing-and-resources).
Gaps may be consumed before the readiness waiter runs, producing a zero tail.

Process allocation, GC and sampled memory cover the controlled cut, pause/checks,
reconnect, acknowledgment hold and full gap drain. Provisioning, initial
registration, later per-path mutations/reads and cleanup are outside this resource
window. The observations include both clients, proxy buffers, decoded path-count
bookkeeping, consumers, metric sampling and the harness. They do not isolate
driver-only allocation or native-server memory.

Memory is sampled every 100 ms and may miss short peaks. The process-lifetime
working-set value also includes provisioning/registration and is `null` when the
platform supplies no positive value. The complete setup duration, process resource
scope, raw wire timestamps and timer frequency are retained. No timing threshold
or stable performance promise follows from one recovery.

## Bounds, evidence and cleanup

Cases have a 240-second managed guard, 15-second recovery/delivery waits, unchanged
five-second ordinary operation deadlines, and a separate 30-second cleanup guard.
The outer process guard is 300 seconds with a five-second termination grace.
Consumer reads and aggregate waits are observed before iterator disposal; proxy
gates are released and all forwarding tasks are stopped and awaited on failure.
Leaf cleanup uses bounded batches and an independent token.

The printed artifact directory retains per-version JSON and gap-histogram CSV,
safe managed logs, settings, source revision, machine/SDK/Docker data, native
versions/roles/configuration, image identities, server logs and cleanup results.
Set `ZKLINK_BENCHMARK_ARTIFACT_ROOT` to choose its parent directory. Managed reports
exclude credentials, session IDs, node paths and data contents.

The independent shell trap verifies fresh authenticated readiness on all three
members and removes the owned fixture and volumes even after process failure.
## Observed local runs

On 28 October 2026, the 10,000-path workload passed on both versions with an Apple
M4 Max/macOS arm64 client, SDK 10.0.403 and .NET 10.0.14. Each case transmitted
10,000 native registrations, restored 10,000 distinct paths in eight frames,
consumed 10,000 reconnect gaps, and verified 10,000 later invalidations and
protected reads. Both 100-path smoke cases also passed with one restore frame.
The [recorded local baseline](../../benchmarks/ZkLink.Benchmarks/baselines/2026-10-28-watch-recovery-osx-arm64.json)
retains all four case reports, source/raw-report hashes and machine/Docker data.
These single-cycle observations establish the stated bounded workload, without
cross-platform or stable-runner timing claims.

Separate failure checks held gap consumption, introduced an unexpected native
change, and exited during the held acknowledgment. They failed as expected and
verified fresh all-node readiness and fixture removal; managed cleanup also
verified namespace removal and zero counters.

The [fan-out](run-watch-benchmarks.md) and [request](run-live-benchmarks.md)
benchmarks remain separate selections.

[How-to guides](README.md) · [Documentation home](../README.md)
