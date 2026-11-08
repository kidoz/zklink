# Production release readiness

Current package versions, tested platforms and capability limits are maintained
in [Current capabilities](current-capabilities.md). Package publication and full
production qualification are separate: the owner-directed publication policy of
7 November 2026 permits release after exact-source candidate, provenance and
installed-consumer checks while sustained qualification remains open.

## Remaining production qualification

The core targets .NET 10, ZooKeeper **3.8.7 and 3.9.6**, and Linux x64/arm64,
Windows x64 and macOS arm64. Existing scoped managed/native results do not close
all production requirements. Optional DI and analyzers have their own evidence;
arbitrary application dependencies are outside the native host checks.

| Requirement | Remaining evidence |
| --- | --- |
| Sustained resource ownership | Complete and review six continuous hours per server patch with workload completion, resource series and verified cleanup, reconciled with the candidate inputs. Interrupted and failed attempts do not count as passes. |
| Historical failure disposition | Review retained DeleteAsync timeouts, restoration/proxy framing failures and fixture observation failures. Bounded controls identify some harness mechanisms, but do not establish every historical cause. |
| Performance | Retain reviewed repeated measurements from the existing protocol, live CRUD, fan-out and recovery commands on a controlled runner. Shared-runner smoke timing establishes no throughput target. |
| Security deployment | Review PKI, revocation availability, certificate rotation and platform cleanup limits. Secure live fixtures use private PKI with NoCheck; dedicated online-revocation responders provide separate scoped evidence. Client TLS does not qualify server quorum encryption. |
| Final candidate | Reconcile public API compatibility, documentation, package bytes, provenance, managed/native consumers and applicable recovery/security evidence with the exact candidate. Behavior changes require review of which checks must repeat. |

The strict production-evidence validator remains separate from the publication
workflow. Green short CI and NuGet availability do not complete these checks.
No full sustained qualification is claimed.

Chronological runs, failure investigations, superseded candidate versions and
publication-policy decisions are preserved in
[Release qualification history](release-qualification-history.md). That record
includes the older six-hour 3.8.7 result, failed 3.9.6 restoration attempt and
later interrupted attempts; none establishes a completed two-patch qualification.

[Current capabilities](current-capabilities.md) · [Reference](README.md)
