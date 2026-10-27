# Run protocol and framing benchmarks

Use the SDK pinned in `global.json`. Docker and Java are unnecessary for these
server-free measurements.

```sh
just bench
```

The default runs all twenty cases with BenchmarkDotNet's `Short` job. Choose a
filter or a longer measurement job with its console arguments:

```sh
just bench --filter '*CodecBenchmarks*' --job Medium
just bench --filter '*FramingBenchmarks*' --job Short
```

The runner restores locked dependencies, builds Release, checks allocation
budgets, then prints the retained artifact directory. Set
`ZKLINK_BENCHMARK_ARTIFACT_ROOT` to choose its parent directory. Each run saves
the SDK, revision and working-tree status, budget file, allocation observations,
BenchmarkDotNet log, full JSON reports, summary tables and raw measurement CSV.
Keep these together when comparing runs.

For the allocation gate alone:

```sh
just bench-allocations
just bench-allocations artifacts/benchmarks/local-allocations.json
```

## What is measured

| Workload | Sizes | Measured work |
| --- | --- | --- |
| Get data | 32 bytes and 64 KiB returned data | Encode request; decode reply into owned data/stat objects |
| Set data | 32 bytes and 64 KiB submitted data | Encode request; decode stat reply |
| Children | 8 and 1,024 names | Encode request; decode child names |
| Transaction | 4 and 32 set-data members, 32 bytes each | Encode multi request; decode member results |
| Framing | 32-byte and 64 KiB payloads | Read a frame into an owned buffer; write prefix and payload |

These are internal protocol measurements. Requests, synthetic Jute replies,
destination buffers, streams and framing objects are constructed once before
measurement. Setup validates decoded content and framed output. Encoding reuses
caller-owned storage; decoding and frame reading include their returned object
and buffer allocations. Get/children request sizes stay constant when only their
response size changes. Transaction encoding includes its validation passes.

Framing uses reusable `MemoryStream` instances that complete synchronously.
Results include framing and copying costs; they do not establish socket, TLS,
scheduler, session or live ZooKeeper operation performance. Server and network
settings are recorded as absent. Live concurrency, watch fan-out and large watch
restoration benchmarks remain separate work.

## Read the results

BenchmarkDotNet's memory diagnoser reports allocated bytes and collection counts.
The tables also include operations per second and p50/p95 timing. These percentiles
describe benchmark iteration measurements, not a distribution of individual live
request latencies. A short job provides an initial local observation; use repeated
runs on a controlled machine for comparisons. Keep power mode, SDK/runtime,
architecture and background load consistent. Raw samples are retained for review.

The separate allocation gate warms each workload for 2,048 calls, then measures
4,096 calls with `GC.GetAllocatedBytesForCurrentThread`. Framing must complete
synchronously on the same thread. No disk I/O, JSON writing or fixture construction
occurs inside a measured loop. The gate writes all numeric observations before
returning a failure for exceeded limits. Missing, invalid or unmatched budgets
also fail. CI runs this gate in a separate job and retains its JSON evidence.

The checked-in [allocation budgets](../../benchmarks/ZkLink.Benchmarks/allocation-budgets.json)
are explicit byte limits, with headroom above the initial measured baseline.
Review a regression before changing a limit. There is no timing threshold in CI.

## Initial local baseline

All twenty cases completed on 28 October 2026 with an Apple M4 Max, macOS 27.0.1,
SDK 10.0.403 and .NET 10.0.14. The
[recorded baseline](../../benchmarks/ZkLink.Benchmarks/baselines/2026-10-28-osx-arm64.json)
contains machine details, input hashes, timings, GC data and both allocation
observations. The allocation gate observed:

| Workload | Encode/write bytes per call | Decode/read bytes per call |
| --- | --- | --- |
| Get, 32 bytes | 0 | 200 |
| Get, 64 KiB | 0 | 65,704 |
| Set, 32 bytes | 0 | 136 |
| Set, 64 KiB | 0 | 136 |
| Children, 8 names | 0 | 624 |
| Children, 1,024 names | 0 | 57,520 |
| Multi, 4 members | 64 | 640 |
| Multi, 32 members | 64 | 4,224 |
| Frame, 32 bytes | 0 | 128 |
| Frame, 64 KiB | 0 | 65,632 |

BenchmarkDotNet observed fewer allocations for the four-member transaction
(32 bytes encoding and 608 bytes decoding). Its longer warmup and generated
harness differ from the short allocation check; retain both observations when
reviewing limits. Timings remain provisional and do not establish cross-platform
or live-server performance.

Implementation details and supported options are documented by
[BenchmarkDotNet's memory diagnoser](https://benchmarkdotnet.org/articles/configs/diagnosers.html),
[exporters](https://benchmarkdotnet.org/articles/configs/exporters.html) and
[console arguments](https://benchmarkdotnet.org/articles/guides/console-args.html).

[How-to guides](README.md) · [Documentation home](../README.md)
