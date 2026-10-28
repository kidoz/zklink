# Measure shared persistent-watch fan-out

Run delivery measurements for 1, 32 and 256 shared persistent-watch consumers on
fresh ZooKeeper 3.8.7 and 3.9.6 fixtures:

```sh
just bench-watches
```

Each case has one second of warmup followed by five seconds of measured work.
Choose 1–30 measured seconds per case; CI uses one second:

```sh
just bench-watches 1
just bench-watches 30
```

Run on Linux or macOS with the pinned .NET SDK, Docker with Compose and Python 3.
The command uses the same owned fixtures, native version/role checks, process
guards, metadata and independent shell cleanup as the
[live request runner](run-live-benchmarks.md). Each case starts a fresh process
and targets the observed leader over local plaintext TCP with explicit digest
authentication. Timings and resources remain informational local observations.

## Workload and delivery checks

One watch-owner session registers distinct logical handles for the same protected
path and persistent mode. A separate authenticated control session mutates a
32-byte payload. The owner uses `MaxWatches` and `MaxBufferedEvents` equal to the
consumer count, with no state subscriptions.

Each round:

1. Arms one read per consumer and rejects an already completed read.
2. Timestamps the control client's set-data invocation and checks its acknowledged
   version and data length.
3. Waits for all consumers, checking event kind, path, registration, connection
   generation and optional zxid against that acknowledgment.
4. Performs sync and protected get-data through the watch owner, checking the
   current data and version.
5. Requires the configured active-handle count and zero event backlog, outstanding
   requests, owned request bytes, tombstones and disconnected-session counters.

Only then does the next mutation start. Every measured mutation must deliver
exactly one event to every consumer. This serialized, known-producer workload
checks fan-out without treating watches as a complete history of arbitrary
concurrent changes. It does not exercise recursive or one-shot watch performance.

Both sessions and their connection generations must remain unchanged. After the
final progress barrier, reads are armed again and must stay pending until owner
close. Close must be acknowledged, all streams must end without another event,
all observed counters must return to zero, and independent cleanup must remove
the namespace. A missing or extra delivery, overflow, request error, unexpected
recovery or cleanup failure fails the run.

## Timing, throughput and resource scope

Latency starts immediately before the mutator's public API invocation and ends
when each consumer's async iterator returns its event. It includes request
admission, server/network work, fan-out and consumer scheduling. It does not isolate
socket arrival or decoder execution.

Reports distinguish mutations per second from logical deliveries per second.
The elapsed interval includes the final round's drain, data validation, progress
requests and counter checks. Registration, warmup, close and namespace cleanup
are outside that interval. Each complete mutation contributes two successful
ordinary owner requests.

All deliveries enter fixed-size histograms. The JSON report includes per-consumer
counts, aggregate p50/p95/p99 bucket ranges, exact-tick mean/max, throughput and
resource observations. CSV files retain every occupied latency bucket and count.
Histogram precision and process allocation/GC/memory accounting follow the
[live measurement definitions](run-live-benchmarks.md#interpret-timing-and-resources).
The shared resource observer includes both clients, metric observation and the
harness; it does not attribute allocation or memory solely to the driver.

Queue checks use a passive listener holding only six current values. There is no
activity history, unbounded latency list or measurement-time file writing. The
reported event budget is the enforced queue limit, rather than a sampled peak.

## Evidence and interruption

The printed directory contains six case reports and histogram CSV files, safe
managed logs, settings, machine/SDK/Docker metadata, server identities and roles,
native logs and cleanup results. Set `ZKLINK_BENCHMARK_ARTIFACT_ROOT` to choose
the artifact parent directory.

Requests and delivery waits have five-second bounds. Case, cleanup and process
guards match the live request runner. Failure cleanup cancels and observes pending
reads before disposing iterators and handles, then disposes the owner and removes
the namespace independently. The shell trap verifies fresh authenticated readiness
on all three servers and removes owned fixtures and volumes even after abrupt
process exit.

## Observed local runs

On 28 October 2026, all six default five-second cases and all six one-second smoke
cases passed with an Apple M4 Max/macOS arm64 client, SDK 10.0.403 and .NET 10.0.14.
The default run validated 973,836 deliveries on 3.8.7 and 1,026,789 on 3.9.6.
Per-consumer counts and histogram totals matched every completed mutation;
progress requests, session ownership, stream termination and cleanup checks passed.
The [recorded local baseline](../../benchmarks/ZkLink.Benchmarks/baselines/2026-10-28-watch-fanout-osx-arm64.json)
retains throughput, latency ranges, process resource observations, input hashes and
raw-report hashes. These are bounded local observations, with no cross-platform
or stable-runner timing claim.

Separate checks deliberately added an unexpected native mutation, held a consumer
past the delivery deadline and exited the process during measured work. All failed
as expected, with independent namespace/counter cleanup where managed cleanup ran,
plus fresh authenticated all-node readiness and fixture removal in every case.

CI runs all six cases with one-second intervals, checking correctness and cleanup
without timing thresholds. The [request benchmarks](run-live-benchmarks.md) remain
separately runnable. Recovery with 10,000 watches and broader performance
qualification remain separate work.

[How-to guides](README.md) · [Documentation home](../README.md)
