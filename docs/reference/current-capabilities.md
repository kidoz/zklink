# Current capabilities

Reviewed against repository records and the public NuGet feed on **23 November
2026**. The latest public release is **1.2.0** for `ZkLink`,
`ZkLink.Extensions.DependencyInjection` and `ZkLink.Analyzers`. The run links on
this page record the 1.1.1 candidate; the changes since then are in the
[changelog](../../CHANGELOG.md). See [production release
readiness](release-readiness.md) for open qualification requirements. No full
production qualification is claimed.

## Capabilities and limits

| Area | Implemented and verified scope | Remaining limits |
| --- | --- | --- |
| Build and packages | `net10.0` package assets for .NET 10 and .NET 11 applications, C# 14, pinned SDK 11.0.100, locked dependencies, deterministic builds and API/package/source/symbol/installed-consumer checks; core has no third-party runtime dependencies | Repeat applicable checks for each candidate; package availability does not establish production qualification |
| Sessions and operations | TCP/TLS, digest replay, one owned session, bounded requests and events, node/stat/children/ACL operations, transactions/read batches, auxiliary queries, explicit TTL opt-in, cancellation outcomes and acknowledged close | Core sessions end on terminal failure. Callers decide whether to retry uncertain requests. Final sustained qualification remains open |
| Watches | One-shot, persistent and recursive watches, shared removal, bounded restoration and explicit reconnect gaps, with controlled-peer and two-patch live evidence | Invalidations are not change history; recursive watches exclude child-list duplicates; compatibility-profile restrictions apply |
| Security | TLS required by default, custom roots, mTLS, digest and explicit ACLs, redaction, and scoped managed/native certificate and online-revocation checks | Plaintext digest requires explicit unsafe opt-in and literal loopback endpoints. Deployment PKI, production certificate rotation, quorum TLS and full security qualification remain open |
| Dependency injection | Optional `AddZkLink` host ownership, singleton accessor, startup/shutdown, health reporting and explicit `Recreate` policy for terminal sessions. The default policy is `Fail` | Applications must rebuild session-dependent state after replacement. Arbitrary application registrations and dependencies require their own checks |
| Analyzers | Seven optional Roslyn diagnostics (`ZKL0001`–`ZKL0007`), with compilation and installed-package checks. `ZKL0007` reports directly discarded state subscriptions | Diagnostics cover documented source patterns |
| Observability | BCL traces/metrics, bounded asynchronous delivery, finite tags, listener isolation and sensitive-data exclusion | Best-effort delivery; exporter integration and sustained-load qualification remain open |
| Performance and resources | Protocol/allocation checks, bounded live CRUD, shared watch fan-out and 10,000-path recovery measurements | Controlled repeated measurements and full sustained qualification remain open; no stable throughput/latency claim |

## Native AOT and platform evidence

Core enables `IsAotCompatible`, and both core and DI enable trim/AOT analyzers.
All 36 jobs passed for the 1.1.1 candidate at source commit `b0f06d4`, including
.NET 10 Native AOT publication, executable identity, online revocation and secure
live checks on **linux-x64, linux-arm64, win-x64 and osx-arm64**. The release
workflow verified public installation and symbols after publication.
[Candidate run](https://github.com/kidoz/zklink/actions/runs/44317716440) ·
[Release run](https://github.com/kidoz/zklink/actions/runs/44321419022)

The repository records [candidate run 42406361077](https://github.com/kidoz/zklink/actions/runs/42406361077)
passing all 36 jobs for the 1.0.2 source `45bd4a0`. The later
[public-package DI run 42410311495](https://github.com/kidoz/zklink/actions/runs/42410311495)
uses public core/DI 1.0.2 packages from that source and harness `c5f9ff5`:
all eight jobs pass, including rooted/unrooted publication without trim/AOT
warnings, direct executable identity and 48 secure native DI lifecycle cases
across the four targets and both server patches.

On macOS arm64, .NET 11 Native AOT consumers of the public core and DI 1.0.3
packages passed rooted and unrooted publication, secure TLS/mTLS cases against
ZooKeeper 3.8.7 and 3.9.6, and online certificate revocation controls. Hosted
.NET 11 Native AOT verification for Linux and Windows remains open.

See [Native AOT verification](../how-to/verify-native-aot.md) for exact scope,
commands and platform limits. Full production and sustained qualification remain
open independently of native publication and execution.

## Server and security scope

Both **ZooKeeper 3.8.7 and 3.9.6** have scoped managed operation, ACL, transaction,
TTL, watch and recovery evidence. The default 3.8 compatibility profile rejects
conflicting watch modes at one path; the 3.9 profile permits independent modes.
TLS/mTLS and required-authentication fixtures are separate from the ordinary
plaintext loopback fixtures. Secure live fixtures use private PKI with `NoCheck`.
Dedicated controlled-peer tests exercise online certificate revocation.
SASL, read-only sessions, administrative reconfiguration and coordination recipes
remain outside the core scope.

Use the [reference index](README.md) for API details and the
[how-to guides](../how-to/README.md) for individual verification commands.
Earlier chronological observations are retained in
[Capability evidence through 1 November](capability-history.md) and
[Release qualification history](release-qualification-history.md).

[Reference](README.md) · [Documentation home](../README.md)
