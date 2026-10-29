# Production release readiness

Reviewed on **29 October 2026**. Core **0.1.0 is published**. The intended next
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

| Requirement | Observed evidence | Still needed for release |
| --- | --- | --- |
| Cross-platform build and packaging | [All 22 CI jobs passed](https://github.com/kidoz/zklink/actions/runs/40882134428), including three OS package-inspection jobs and six Linux installed-consumer cases | Repeat applicable checks against the final candidate and compare compatibility with released 0.1.0 |
| Native execution | [Four-target native run passed](https://github.com/kidoz/zklink/actions/runs/40896496841): 48 secure live cases with reviewed artifacts | Test the exact final package on every target; close native sent-cancellation/unknown-outcome coverage |
| Production TLS policy | [Managed default-online revocation checks](https://github.com/kidoz/zklink/actions/runs/40900036928/attempts/1) pass on Linux x64/arm64, Windows x64 and macOS arm64; platform downloads are bounded where supported and cancellation is rechecked before transport transfer | Qualify the final candidate and native online-revocation paths; review deployment PKI and documented platform cleanup limits. The ZooKeeper/native fixtures still use `NoCheck`. Triage the previously unexplained concurrent rejection timeout |
| Required recovery coverage | Scoped follower, leader, quorum, client-traffic and peer-partition checks pass | Add observer-endpoint evidence; audit required authentication, expiry, cancellation and watch cases against actual observations and fix gaps |
| Sustained resource ownership | Short seeded workloads pass; the release-duration attempt failed after about two hours on 3.8.7 with a `SyncAsync` timeout | Investigate the retained timeout, then complete both durations and review heap, threads, sockets, counters and cleanup. Reconcile measured runtime inputs with the candidate |
| Performance evidence | Existing protocol/allocation, live CRUD, watch fan-out and 10,000-watch recovery suites produce raw reports | Repeat existing commands on a controlled runner and retain a reviewed report. No throughput target or timing gate is inferred from shared-runner smoke results |
| Final artifact and publication | Core publication, symbols, provenance and fresh-consumer verification have prior 0.1.0 evidence | Build one final candidate and use its package hash across managed/native checks; enforce the complete release-evidence checks before publishing 1.0.0 |
| API and operator documentation | The shipped baseline records 0.1.0; usage and failure semantics are documented | Review compatibility and intentional changes, freeze the 1.0.0 API, reconcile defaults/support claims and document the tested release matrix |

For the TLS change, 22 hosted jobs passed. macOS package inspection could not
acquire a runner; its queued retry was cancelled after the same command passed
locally on macOS arm64 from the verified commit. This is scoped evidence, not
an entirely successful hosted workflow.

The current release workflow packs local candidates separately for native
checks, then builds the versioned publication package later. It also runs a
short CI soak rather than checking completed release-duration evidence. These
are gaps in the release process even when every current job passes.

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
