# Production release readiness

On **7 November 2026**, the owner requested a HostLoom-style NuGet release without
the remaining soak tests and benchmarks. NuGet publication now requires a
successful exact-source candidate, verified package provenance and installed
consumer checks; sustained qualification remains separate. The current candidate
[passes all 36 jobs](https://github.com/kidoz/zklink/actions/runs/42377862412).
The interrupted soak is retained as incomplete, and controlled benchmarks were
not run. NuGet availability does not establish full production qualification.

Reviewed on **2 November 2026**. Core **0.1.0 is published**. The intended next
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
| Production TLS policy | Managed and native checks cover valid, revoked, unavailable and invalidly signed status responses, plus held-response cancellation, on all four targets | Review deployment PKI and documented platform cleanup limits. The live ZooKeeper fixtures use `NoCheck`; online policy has dedicated responders. Review the disposition of the unexplained concurrent rejection timeout; bounded investigations pass without identifying its cause. A local Mac with an active network filter does not fetch loopback status; its cause remains unconfirmed |
| Required recovery coverage | Scoped follower, leader, quorum, client-traffic and peer-partition checks pass; live expiry/authentication checks cover both versions. Reviewed cancellation/watch evidence includes 194 controlled cases per managed target and 48 matching-candidate secure native cases | Reconcile the completed recovery reviews with final release evidence. Historical failure disposition, sustained qualification and controlled measurements remain required |
| Sustained resource ownership | The latest 3.8.7 workload completed six hours; 3.9.6 failed after about 90 minutes waiting for watch restoration, with a proxy framing error. The older two-hour `DeleteAsync` timeout remains unresolved | Investigate the retained timeout/frame failures, retain the corrected initial-retry and cross-session write-fence checks, then complete both durations and review heap, threads, sockets, counters and cleanup. Reconcile measured runtime inputs with the candidate |
| Performance evidence | Existing protocol/allocation, live CRUD, watch fan-out and 10,000-watch recovery suites produce raw reports | Repeat existing commands on a controlled runner and retain a reviewed report. No throughput target or timing gate is inferred from shared-runner smoke results |
| Final artifact and publication | Shared-candidate distribution passes hosted verification. Public-feed installation and symbol-server verification pass against published 0.1.0 | Complete final-candidate qualification, then publish those exact package bytes |
| API and operator documentation | The shipped baseline records 0.1.0; new null-preserving array overloads are unshipped additions. The unpublished DI API now exposes a singleton client accessor; usage and failure semantics are documented | Review behavioral compatibility and intentional changes, freeze the 1.0.0 API, reconcile defaults/support claims and document the tested release matrix |

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

The concurrent TLS check now retains bounded per-connection handshake timing,
individual certificate-import progress and safe failure classifications. Each
rejection burst also opens a fresh valid connection and verifies that its client
connections drain back to the existing control. Functional and concurrent
matrices pass on macOS and Linux; transport/trace checks pass on all four managed
targets in [hosted verification](https://github.com/kidoz/zklink/actions/runs/41309147708).
Current-environment controls with the original class overlap, connection budgets
and an earlier TLS connector also pass. The original temporary logs are
unavailable, so the historical timeout remains unexplained and requires explicit
release review.

The latest sustained attempt is now **failed**. Its 3.9.6 recovery proxy rejected
its first client frame before the source connection reached readiness; the
retained prefix is consistent with a TLS handshake record arriving at the
plaintext listener. This is an inference from four bytes, not a complete decoded
handshake or sender attribution. The restore wait then expired, and disposal
surfaced the framing error. All 468,251 CRUD operations had succeeded. Three-node
authenticated rollback and owned teardown completed. Preserve the six-hour 3.8.7
summary and both failure records; the whole attempt does not qualify the release.
Investigate the unexpected peer traffic before another full run. No automatic
restart or host-network-policy change has been made.

Follow-up diagnostics now retain the listener, peer and upstream ports and can
query the live peer's PID and process name on macOS/Linux when `lsof` is available.
Other platforms and unavailable lookups report their status explicitly. The lookup
does not collect arguments, environments or packet bodies, and malformed frames
remain fatal. Controlled TLS contamination and failure-evidence checks pass on
all four managed targets in [hosted verification](https://github.com/kidoz/zklink/actions/runs/41323430531).
The original sender has not been identified. A bounded loopback probe did not
reproduce unsolicited traffic. The maintained `just zk-soak-isolated` command now
keeps proxy ports inside an owned follower network namespace, with a pinned Linux
runtime, source/binary integrity checks, continuous resource observations and
bounded owned cleanup. Native and isolated short workloads and all four probes for driver termination
or supervisor interruption pass on both server versions locally and in
[Linux CI](https://github.com/kidoz/zklink/actions/runs/41332643283).
The isolated process evidence covers Linux x64/arm64; it does not explain the old
sender or qualify native macOS/Windows resource behavior. A fresh supervised
six-hour run on each patch, candidate reconciliation and explicit historical
failure review remain required. No full-duration restart or benchmark run was
performed during this runner verification.

On 1 November, a fresh supervised isolated attempt started after short checks
passed on both patches. It requests six continuous hours per patch using frozen
inputs that match the verified runner and candidate core sources. Full completion,
cleanup, resource trends and applicability to the candidate still require review;
the launch does not waive the retained failures or qualify the release.

That attempt stopped on 3.8.7 after about three and a half minutes at the
socket-cleanup check. An instrumented reproduction found a foreign listener
being counted by port number. A separate Docker DNS control recreates the
same-port collision on another loopback address. The observer now tracks original
socket handles and exact endpoint pairs; retained-listener and retained-peer
controls remain failures under the unchanged guard. All 67 proxy/socket/evidence
checks pass on each of four managed targets in
[hosted verification](https://github.com/kidoz/zklink/actions/runs/41351171320),
along with Docker namespace and owned failure-cleanup checks. The original failed
run lacks its socket row, so its exact socket cannot be identified retrospectively.
Fresh full-duration evidence and the remaining failure reviews are still required.

Subsequent five-minute workloads passed on both patches with the corrected
observer, clean resource checkpoints and verified teardown. A fresh supervised
six-hour-per-patch attempt has been launched from frozen inputs; its completion
and review remain pending. Benchmarks stay deferred until sustained qualification.

The release-candidate workflow now distributes one package/symbol bundle to
managed and native consumers. Publication downloads those qualified bytes,
checks the completed candidate run and attestations, and rejects missing or
mismatched production evidence. Its fresh public-feed and symbol-server check
has passed against the published 0.1.0 release. Hosted verification of the shared
candidate path passes; these changes do not complete sustained qualification.

The working tree now prepares version 1.0.0 and enables AOT compatibility
analysis for that unpublished candidate. The 401 declarations in the shipped
0.1.0 baseline are preserved; the candidate adds five null-preserving array
overloads in the unshipped baseline. These API changes have local managed and
installed-package evidence and require a fresh candidate for final managed/native
qualification. Older evidence remains scoped to its recorded inputs. Neither the
metadata nor these passing checks authorize publication before the remaining
evidence is complete.

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
