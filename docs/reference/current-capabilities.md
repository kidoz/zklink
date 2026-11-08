# Current capabilities

Reviewed against repository records and the public NuGet feed on **8 November
2026**. The documented public release is **1.0.3** for `ZkLink`,
`ZkLink.Extensions.DependencyInjection` and `ZkLink.Analyzers`. See the
[changelog](../../CHANGELOG.md) for changes and [production release
readiness](release-readiness.md) for open qualification requirements. No full
production qualification is claimed.

## Capabilities and limits

| Area | Implemented and verified scope | Remaining limits |
| --- | --- | --- |
| Build and packages | .NET 10, C# 14, pinned SDK 10.0.403, locked dependencies, deterministic builds and API/package/source/symbol/installed-consumer checks; core has no third-party runtime dependencies | Repeat applicable checks for each candidate; package availability does not establish production qualification |
| Sessions and operations | TCP/TLS, digest replay, one owned session, bounded requests/events, node/stat/children/ACL operations, transactions/read batches, auxiliary queries, explicit TTL opt-in, cancellation outcomes and acknowledged close | No automatic replacement of a terminal session or replay of uncertain requests; final sustained qualification remains open |
| Watches | One-shot, persistent and recursive watches, shared removal, bounded restoration and explicit reconnect gaps, with controlled-peer and two-patch live evidence | Invalidations are not change history; recursive watches exclude child-list duplicates; compatibility-profile restrictions apply |
| Security | TLS required by default, custom roots, mTLS, digest and explicit ACLs, redaction; scoped managed/native certificate and online-revocation controls | Plaintext digest requires explicit unsafe opt-in and literal loopback endpoints only; DNS names including localhost and remote/mixed lists are rejected in this checkout. Deployment PKI, rotation and full security qualification remain separate |
| Dependency injection | Optional AddZkLink host ownership, singleton accessor, startup/shutdown and non-owning consumer views; public 1.0.2 installed/native host checks | Arbitrary application registrations/dependencies require their own checks |
| Analyzers | Six optional ZKL diagnostics, compilation tests and installed-package diagnostic checks | Diagnostics cover documented source patterns, not complete program analysis |
| Observability | BCL traces/metrics, bounded asynchronous delivery, finite tags, listener isolation and sensitive-data exclusion | Best-effort delivery; exporter integration and sustained-load qualification remain open |
| Performance and resources | Protocol/allocation checks, bounded live CRUD, shared watch fan-out and 10,000-path recovery measurements | Controlled repeated measurements and full sustained qualification remain open; no stable throughput/latency claim |

## Native AOT and platform evidence

Core enables `IsAotCompatible`; core and DI enable trim/AOT analyzers. Native AOT
has scoped packed-consumer evidence on **linux-x64, linux-arm64, win-x64 and
osx-arm64**. This is available verification, not an unimplemented feature.

The repository records [candidate run 42406361077](https://github.com/kidoz/zklink/actions/runs/42406361077)
passing all 36 jobs for the 1.0.2 source `45bd4a0`. The later
[public-package DI run 42410311495](https://github.com/kidoz/zklink/actions/runs/42410311495)
uses public core/DI 1.0.2 packages from that source and harness `c5f9ff5`:
all eight jobs pass, including rooted/unrooted publication without trim/AOT
warnings, direct executable identity and 48 secure native DI lifecycle cases
across the four targets and both server patches. The live fixtures use private
PKI with `NoCheck`. Dedicated core online-revocation controls are separate.
These recorded runs cover the 1.0.2 packages and do not verify the published
1.0.3 source.

See [Native AOT verification](../how-to/verify-native-aot.md) for exact scope,
commands and platform limits. Full production and sustained qualification remain
open independently of native publication and execution.

## Server and security scope

Both **ZooKeeper 3.8.7 and 3.9.6** have scoped managed operation, ACL, transaction,
TTL, watch and recovery evidence. The default 3.8 compatibility profile rejects
conflicting watch modes at one path; the 3.9 profile permits independent modes.
TLS/mTLS and required-authentication fixtures are separate from the ordinary
plaintext loopback fixtures. Client TLS makes no quorum-encryption claim.
SASL, read-only sessions, administrative reconfiguration and coordination recipes
remain outside the core scope.

Use the [reference index](README.md) for API details and the
[how-to guides](../how-to/README.md) for individual verification commands.
Earlier chronological observations are retained in
[Capability evidence through 1 November](capability-history.md) and
[Release qualification history](release-qualification-history.md).

[Reference](README.md) · [Documentation home](../README.md)
