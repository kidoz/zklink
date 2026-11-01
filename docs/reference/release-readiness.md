# Production release readiness

Reviewed on **1 November 2026**. Core **0.1.0 is published**. The intended next
production release is **1.0.0**, subject to the checks below. It is not yet
qualified for production. Passing the current CI workflows establishes their
documented scope; it does not complete these release requirements.

## Release scope

Qualify the core `ZkLink` package for .NET 10, ZooKeeper **3.8.7 and 3.9.6**, and
Linux x64/arm64, Windows x64 and macOS arm64 clients. Cover the implemented node,
ACL, transaction, watch and session APIs, including explicit uncertain outcomes,
reconnect gaps, bounded resource ownership and acknowledged close. Required
connection coverage includes TLS, mTLS, digest and observer endpoints.

Optional DI and analyzer packages retain separate qualification. SASL, read-only
sessions, administrative reconfiguration and coordination recipes remain outside
the core release. Quorum encryption is a server deployment concern; it must not
be implied by client TLS evidence. These exclusions do not remove any required
core acceptance check.

## Evidence and blockers

Release qualification requires at least **six continuous hours on each server
version**, with complete resource series, normal workload completion and verified
cleanup. Longer completed runs up to 24 hours remain optional evidence. This
replaces the earlier 24-hour-per-version requirement and accepts less
long-duration coverage. It does not resolve historical failures or remove any
correctness, security, resource, benchmark or publication requirement.

| Requirement | Observed evidence | Still needed for release |
| --- | --- | --- |
| Cross-platform build and packaging | [All 33 shared-candidate jobs passed](https://github.com/kidoz/zklink/actions/runs/41239985235), including three OS package-inspection jobs and Linux installed consumers | Repeat applicable checks after final metadata changes, including the released-package comparison |
| Native execution | The same shared-candidate run passes all four native targets, 48 secure live cases with sent-cancellation/`Unknown`, and five online-revocation controls per target | Repeat after final metadata changes and complete sustained qualification |
| Production TLS policy | Managed and native checks cover valid, revoked, unavailable and invalidly signed status responses, plus held-response cancellation, on all four targets | Review deployment PKI and documented platform cleanup limits. The live ZooKeeper fixtures use `NoCheck`; online policy has dedicated responders. Triage the previously unexplained concurrent rejection timeout. A local Mac with an active network filter does not fetch loopback status; its cause remains unconfirmed |
| Required recovery coverage | Scoped follower, leader, quorum, client-traffic and peer-partition checks pass; live expiry/authentication checks cover both versions. Reviewed cancellation/watch evidence includes 194 controlled cases per managed target and 48 matching-candidate secure native cases | Reconcile the completed recovery reviews with final release evidence. Historical failure disposition, sustained qualification and controlled measurements remain required |
| Sustained resource ownership | Short seeded workloads pass; the release-duration attempt failed after about two hours on 3.8.7 with a `DeleteAsync` timeout | Investigate the retained timeout/frame failures, retain the corrected initial-retry and cross-session write-fence checks, then complete both durations and review heap, threads, sockets, counters and cleanup. Reconcile measured runtime inputs with the candidate |
| Performance evidence | Existing protocol/allocation, live CRUD, watch fan-out and 10,000-watch recovery suites produce raw reports | Repeat existing commands on a controlled runner and retain a reviewed report. No throughput target or timing gate is inferred from shared-runner smoke results |
| Final artifact and publication | Shared-candidate distribution passes hosted verification. Public-feed installation and symbol-server verification pass against published 0.1.0 | Complete final-candidate qualification, then publish those exact package bytes |
| API and operator documentation | The shipped baseline records 0.1.0; SDK comparison and the 401-entry declaration review find no API changes; usage and failure semantics are documented | Review behavioral compatibility and intentional changes, freeze the 1.0.0 API, reconcile defaults/support claims and document the tested release matrix |

The current package checks pass on Linux, Windows and macOS, including the
published 0.1.0 baseline and expected breaking-change rejections. The independent
timeout replay stopped after about 45 minutes on 3.8.7 with a primary wait timeout
and a proxy framing error during cleanup, before its first hourly cleanup.
Both errors and successful fixture rollback are retained. New diagnostics label
guarded waits and retain rejected-frame metadata at the original observation
time. Fresh five-minute workloads pass on both versions with these diagnostics;
the replay failure and original two-hour timeout remain unresolved.

Controlled checks reproduce a reporting defect in the fault harness: a terminal
proxy framing error can leave a recovery signal pending until its guard expires,
then surface again during disposal. The revised waits propagate the original
error and its observation metadata before cleanup. This identifies a masking
mechanism; the old run did not retain the rejected frame metadata needed to
identify its trigger. Separate deadline checks cover committed Delete replies,
explicit uncertain outcomes and ordered late-reply recovery without replay.
[Hosted verification passed](https://github.com/kidoz/zklink/actions/runs/41293302220)
on all four managed targets and both pinned server versions. These bounded
experiments do not establish the cause of either historical failure.

The release-candidate workflow now distributes one package/symbol bundle to
managed and native consumers. Publication downloads those qualified bytes,
checks the completed candidate run and attestations, and rejects missing or
mismatched production evidence. Its fresh public-feed and symbol-server check
has passed against the published 0.1.0 release. Hosted verification of the shared
candidate path passes; these changes do not complete sustained qualification.

The working tree now prepares version 1.0.0 and enables AOT compatibility
analysis for that unpublished candidate. The 401 public API declarations remain
unchanged from the shipped 0.1.0 baseline. Neither the metadata nor these passing
checks authorize publication before the remaining evidence is complete.

Verification after the metadata change exposed a watch-pressure counter timeout
before the next round's event burst. Earlier rounds drained to zero and fixture
rollback completed. Setup now completes its writes on the watched session before
registration, and counter timeouts retain expected and observed numeric values.
Checks against the pinned Apache implementations confirm that a delayed setup
commit can legitimately produce a watch event after registration. Applying setup
writes on the watched session prevents that precondition error. The original
failure lacks actual counter values, so its exact attribution remains unconfirmed;
the corrected check and a reviewed failure record are required before release.

That preceding candidate run also received `NotEmpty` while deleting a round's
namespace on 3.9.6, after 534 completed rounds and 11,755 successful CRUD requests.
The original workload assertions had passed; the cleanup failure remains fatal.
Follow-up diagnostics preserve child counts, child-version/transaction metadata
and fixed node-category counts before and after the rejected delete, without
retaining names. Deterministic Apache-only checks reproduce an empty follower
view alongside a leader-side `NotEmpty` rejection while session-close child-count
bookkeeping is pending. The revised fixture binds maintenance to the observed
leader and uses its acknowledged write fences before independent post-close
observations and cleanup. A controlled-peer regression preserves the explicit
`Rejected` result without replay, even after an empty child-list snapshot.
The original failed record lacks the node counts needed for conclusive historical
attribution. Local short runs interrupted by idle sleep are retained as interrupted evidence;
continuous qualification rejects their UTC reporting gaps.

The later replay stopped after about 76 minutes because its test required
exactly one connection attempt, including initial attempts. A controlled test
now proves that an initial handshake may time out and retry before readiness.
The revised assertion still rejects unexpected attempts after readiness.
Hosted verification also exposed an independent-session read assumption;
verification now uses an acknowledged quorum write before asserting the single
committed child. Original failed attempts remain retained. The interrupted
full-duration attempt is incomplete and must not be counted as a pass.

## Delivery order

1. **Close correctness and security blockers.** Investigate the retained long-soak
   timeout without waiving its assertions. Managed online-policy checks now
   cover valid/revoked/unavailable status, invalid signatures and cancellation;
   retain those checks and their platform limits. Complete observer
   and required native request-outcome coverage. Investigate required-case
   failures without weakening assertions or labeling unexplained failures as
   acceptable retries.
2. **Finish sustained qualification.** Investigate the failed soak using its
   retained resource series, stack trace and server logs before repeating the
   release-duration run. Driver behavior changes require a decision about which
   qualification must be repeated. Use the existing benchmark commands after
   the soak, or on a separate controlled machine. Additional benchmark
   orchestration is optional unless it prevents collecting required evidence.
3. **Freeze and validate one release candidate.** Review the public API against
   0.1.0, record intentional changes, then prepare the version, package
   description, release notes and compatibility metadata. Pack once, retain
   checksums and run managed and rooted/unrooted native consumers against those
   same package bytes across the declared matrix. Produce a release-evidence
   record linking the candidate, runtime inputs, tests, soak and measurements.
4. **Enforce and publish the qualified result.** Missing, stale, mismatched or
   failed required evidence must block publication. Verify provenance, the
   tested support matrix and the fresh installed consumer for the resulting
   release. Registry publication requires a separate explicit instruction.

No version change, compatibility flag or green build substitutes for these
observations. Release readiness requires all mandatory core checks to pass;
missing evidence remains an open requirement rather than a support claim.

[Current capabilities](current-capabilities.md) · [Reference](README.md)
