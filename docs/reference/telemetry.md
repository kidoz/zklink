# Tracing and metrics

ZkLink provides a BCL `ActivitySource` and `Meter`, both named **`ZkLink`** and
versioned with the core assembly. Subscribe by name using `ActivityListener`,
`MeterListener`, or your application's telemetry provider. The driver adds no
exporter or OpenTelemetry runtime dependency.

## Traces

All activities have `ActivityKind.Client`.

| Activity | Interval |
| --- | --- |
| `zklink.connect` | Initial connection through readiness, including cleanup on failure |
| `zklink.transport.connect` | One DNS/TCP/TLS connection attempt |
| `zklink.authenticate` | Authentication and acknowledgment on one connection |
| `zklink.restore` | Watch restoration on one connection, including an empty restore |
| `zklink.operation` | Scheduler preparation/admission through caller completion |
| `zklink.close` | The first close call through local cleanup |

Tags are `operation` for an application operation, `lifecycle` for other
activities, and `outcome`/`error`. Operation names come from `ZooKeeperOperation`.
Operation outcomes are `Success`, `NotSent`, `Rejected`, and `Unknown`;
Close failures preserve their request outcome when available; other failed
lifecycle work uses `Failed`. Errors use fixed categories such as
`Cancelled`, `Timeout`, `Backpressure`, `Tls`, `Connection`, and `Protocol`.
Successful activities have status `Ok`; failures have status `Error` without an
exception message, status description, or exception event.

A sent cancellation or timeout completes the caller with `Unknown` and emits
one operation result. Its tombstone remains outstanding until a reply or
detachment drains it. A late reply does not emit another result. Internal watch
cleanup requests count as operations. Synchronous public validation before the
scheduler is excluded; close has its own activity and repeated close calls reuse
the first result.

Completed activities are delivered asynchronously. Their timestamps and durations
describe the original work, excluding dispatcher delay. They do not appear as
live in-flight activities. Only the caller's parent trace/span IDs and trace
flags are retained; baggage, tracestate and parent tags are not copied. Activities
are correlated with the caller, without injecting trace headers into ZooKeeper.

## Metrics

Observable totals are collected when the listener polls. Durations are histograms
in seconds. Current values aggregate clients without a client or session label.

| Instrument | Type | Meaning |
| --- | --- | --- |
| `zklink.operation.count` | Observable counter | Caller completions by operation and outcome |
| `zklink.operation.duration` | Histogram | Caller duration by operation, outcome and error |
| `zklink.operation.unknown` | Observable counter | Operations completed with an unknown outcome |
| `zklink.lifecycle.duration` | Histogram | Connection/authentication/restoration/close durations by lifecycle, outcome and error |
| `zklink.requests.outstanding` | Observable up/down counter | Queued and sent owned requests, including sent tombstones and control requests |
| `zklink.requests.bytes` | Observable up/down counter | Bytes charged to the scheduler's request budget, including buffers still being written |
| `zklink.requests.tombstones` | Observable up/down counter | Sent requests whose callers have completed but whose replies remain pending |
| `zklink.reconnect.attempts` | Observable counter | Connection attempts after a session identity has been assigned |
| `zklink.sessions.disconnected` | Observable up/down counter | Sessions disconnected while recovering |
| `zklink.session.disconnected.duration` | Histogram | Disconnected intervals ending at readiness, close or a terminal state |
| `zklink.session.negotiated_timeout` | Histogram | Server-negotiated session timeout on each accepted handshake |
| `zklink.heartbeat.roundtrip` | Histogram | Writer selection through a valid ping acknowledgment |
| `zklink.watches.active` | Observable up/down counter | Acknowledged, active logical watch registrations |
| `zklink.events.backlog` | Observable up/down counter | Buffered watch and state events, including initial state snapshots |
| `zklink.event.overflows` | Observable counter | Client event-budget faults |
| `zklink.telemetry.dropped` | Observable counter | Diagnostic records rejected by the bounded dispatcher |
| `zklink.telemetry.listener_errors` | Observable counter | Diagnostic deliveries interrupted by a listener exception |

Disposal removes a client's remaining gauge contribution once. Reading or
disposing its completed streams afterward cannot subtract that contribution
again. Aggregate instruments are independent observations, not one atomic
snapshot of all clients.

## Delivery and privacy

A process-owned worker delivers completed activities and histogram samples from
a queue limited to 1,024 records. It registers the source/instruments and delivers records outside driver locks
and the socket reader call stack. A full queue drops diagnostic records and increments
`zklink.telemetry.dropped`; operation/outcome/reconnect/overflow totals and current
pressure remain available through atomic observable instruments.

Listeners should return promptly. A blocked listener stalls diagnostic delivery
and can fill the queue, while driver I/O and close continue. Delivery and registration callback exceptions
are counted without exporting their text. A failed registration can leave the
affected source or instrument unavailable for the process lifetime. Driver completion does not wait for
diagnostic delivery; process exit can discard queued records. Traces and
histograms are best-effort diagnostics, not a durable operation audit.

The driver excludes paths, endpoints, session IDs, request IDs, credentials,
certificate material and znode data. Metric tags have a fixed vocabulary, avoiding
one series per path or client. The application controls its own exporters and
any additional application telemetry.

Controlled listener tests cover outcomes, redaction, recovery, heartbeat timing,
queue pressure, event overflow and blocked/throwing listeners. Local live checks
cover operations, ACL rejection and watch restoration on ZooKeeper 3.8.7 and
3.9.6, plus TLS rejection traces in both secure fixtures. These results do not
establish runtime performance, Native AOT or other client-platform support.

See the BCL guides for [metrics](https://learn.microsoft.com/en-us/dotnet/core/diagnostics/metrics-instrumentation)
and [activity creation](https://learn.microsoft.com/en-us/dotnet/api/system.diagnostics.activitysource.startactivity?view=net-10.0),
and the project's [verification scope](../explanation/verification.md).

[Reference](README.md) · [Documentation home](../README.md)
