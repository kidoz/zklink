# Measure concurrent live requests

Run get/set measurements against fresh three-node ZooKeeper 3.8.7 and 3.9.6
fixtures:

```sh
just bench-live
```

The default measures each case for five seconds after one second of warmup.
Choose 1–30 seconds per case; the CI smoke uses one second:

```sh
just bench-live 1
just bench-live 30
```

Run on Linux or macOS with the pinned .NET SDK, Docker with Compose, and Python 3. Host Java and Gradle
are unnecessary. Missing prerequisites fail the command. Each version runs all
twelve combinations of:

- Get and set operations.
- 32-byte and 64 KiB payloads.
- 1, 32 and 256 concurrent workers sharing one authenticated client/session.

## Workload and correctness

Each version has a unique labeled disposable fixture with dynamic loopback ports.
The runner checks exact versions, authenticated create/read/delete readiness on
all three members, and one leader plus two followers. Requests target the observed
leader; final role checks require that membership to remain unchanged.

Each case starts a fresh process. An independent control client creates a protected
namespace and one node per worker. One measured client performs all workload calls.
Workers start together, issue one request at a time, and start their next request
after the previous result has been checked. There is no session pool. The report
records configured concurrency and `PeakOutstandingCalls`, which counts occupied
worker call slots including result validation and measurement bookkeeping.

Every get checks returned data. Sets alternate between two payloads and check the
returned version and data length. After measurement, synchronized control reads
check every final payload and version. The measured session and connection
generation must remain unchanged, close must be acknowledged, and independent
cleanup must remove the namespace. Errors, unexpected recovery, incomplete workers
or cleanup failure fail the run.

The fixture uses plaintext TCP and an explicit development-only digest opt-in.
Nodes use authenticated-owner ACLs and fresh credentials. Managed reports exclude
credentials, session IDs, node paths and payload contents. These results do not
qualify TLS performance.

## Interpret timing and resources

Setup, authentication, warmup, final validation and close occur outside the measured
interval. Workers stop starting calls when the requested duration expires and
finish the requests already in flight. Throughput divides successful calls by the
actual elapsed time including that drain and worker scheduling. Validation and
measurement bookkeeping contribute to workload cost.

Each call is timed with monotonic timestamps around the public API invocation and
awaited completion. Latency includes admission, transport and client scheduling.
This is a closed-loop workload: it does not simulate an independent arrival rate
or establish service-level latency under overload.

All successful measured calls enter fixed-size histograms, with no measurement-time
file I/O or unbounded sample history. The histogram has single-microsecond buckets
below 32 microseconds and 32 subdivisions per doubling afterward. Values are
rounded up to at least one microsecond; p50/p95/p99 are reported as bucket ranges. The report
also gives the exact-tick mean and maximum. CSV files contain the raw bucket bounds
and counts, covering the complete measured distribution.

Allocation deltas use process-wide
[`GC.GetTotalAllocatedBytes(true)`](https://learn.microsoft.com/en-us/dotnet/api/system.gc.gettotalallocatedbytes?view=net-10.0)
before and after the asynchronous workload and sampler shutdown. They include
managed allocations in the driver, both clients, the harness and sampler. Dividing
by successful calls provides an aggregate observation, not a driver-only allocation
budget. GC counts use the same window. Native allocations are outside this count.

Memory is sampled every 100 ms. Reports distinguish sampled peak working set,
sampled managed heap and the
[process-lifetime working-set high-water mark](https://learn.microsoft.com/en-us/dotnet/api/system.diagnostics.process.peakworkingset64?view=net-10.0).
Sampling can miss short peaks; the lifetime value includes setup and warmup.
The lifetime field is `null` when the platform returns no positive high-water value. Fresh
processes prevent one case's lifetime peak from carrying into another case.
Histograms and other harness state contribute to memory. No forced collection
occurs during measurement.

These numbers describe the recorded local machine, Docker resources, bridge,
server configuration and health checks. Repeat measurements on a controlled host
before comparing performance or setting timing thresholds. CI checks completion,
data, session ownership, close and cleanup; timings and resource numbers are
informational.

## Evidence and interruption

The printed artifact directory retains per-case JSON reports, raw histogram CSV,
safe logs, requested settings, SDK/revision, Docker host resources, Compose settings,
image/container identities, server roles, native logs and cleanup results. Set
`ZKLINK_BENCHMARK_ARTIFACT_ROOT` to choose its parent directory.

Requests have five-second deadlines. A case has its requested duration plus sixty
seconds for managed setup/work, independent cleanup has twenty seconds, and the
outer process guard allows the requested duration plus ninety seconds, with a
five-second termination grace before forced termination. An
interrupted or expired process guard terminates its owned process group. The shell
exit trap retains server logs, runs fresh authenticated readiness probes, and
removes the owned fixture and volumes, including after process failure.

## Observed local runs

The five-second matrix completed all 24 cases on 28 October 2026 with an Apple
M4 Max/macOS arm64 client, SDK 10.0.403 and .NET 10.0.14. It validated 285,486
measured calls on 3.8.7 and 297,453 on 3.9.6. The one-second smoke matrix also
passed on both versions. Every successful case retained its session, checked
final data, acknowledged close and removed its namespace. The
[recorded local baseline](../../benchmarks/ZkLink.Benchmarks/baselines/2026-10-28-live-osx-arm64.json)
contains throughput, percentile ranges, GC/resource observations, machine and
Docker settings, and raw-report hashes. These are bounded local observations,
with no cross-platform or stable-runner timing claim.

Deliberate exception and abrupt-exit checks during a measured call failed as
expected and verified fresh all-node readiness plus owned fixture removal. A
separate process-group interruption check verified descendant termination.

The [protocol/framing suite](run-benchmarks.md) remains independently runnable.
[Shared watch fan-out](run-watch-benchmarks.md) has a separate measurement command.
[Distinct-path watch restoration](run-watch-recovery-benchmarks.md) has a dedicated
runner. TLS performance and broader release qualification remain separate work.

[How-to guides](README.md) · [Documentation home](../README.md)
