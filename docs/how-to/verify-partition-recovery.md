# Verify recovery from one-way client traffic loss

Run:

```sh
just zk-partition-faults
```

Use the SDK pinned in `global.json`, Docker with Compose and Python 3. Host Java
and Gradle are unnecessary; readiness tools run inside the pinned server images.

The command runs incoming and outgoing traffic loss independently, with short
and longer interruptions on ZooKeeper 3.8.7 and 3.9.6: eight cases in total. Each
direction/outcome/version gets a fresh labeled three-voter fixture and an authenticated
namespace. All servers must report the exact version, pass authenticated
create/read/delete probes and report one leader and two followers before testing.
The tested client connects through a loopback proxy to the observed leader;
an independent client connects directly for synchronized control observations.

## Incoming loss

The proxy consumes and discards complete server-to-client ZooKeeper frames,
including reconnect handshake replies. Outgoing application frames and actual
heartbeats continue to reach the healthy server. Before enabling this fault,
one successful sequential-create reply is withheld while its transport stays
open. A synchronized direct control read proves commitment. Exactly one matching
child must remain after recovery, with one create transmission beyond baseline.

## Outgoing loss

The proxy consumes and discards client requests, heartbeats and reconnect
handshakes without forwarding them upstream. Incoming delivery stays open.
The test submits one sequential create after enabling this fault and requires
exactly one discarded create, no additional create transmission and zero matching
children on the server before and after healing.

An independent control mutation then delivers a real persistent-watch event to
the owner while outgoing traffic is still blocked. This proves the incoming
direction works. Silence timing starts after that event; the fixture sends no
further matching mutation until recovery. A dropped heartbeat, without any
forwarded heartbeat during the fault, must precede disconnection. This case does
not claim that outgoing loss always causes inbound silence when notifications
continue arriving.

Both directions leave TCP control acknowledgments active and do not deliberately
close the connection when the fault starts. Discarded frames are zeroed and never
queued for delivery after healing. No server is stopped and no firewall or
inter-server link is changed.

In both cases the pending create must return `Unknown` on transport recovery,
rather than `NotSent`, server rejection or an operation timeout. The client sent
its request into TCP and cannot know the proxy discarded it. External evidence
of server absence does not change the client's knowledge. Discarded writes must
never be forwarded later or replayed after healing.

## Short interruption

The client requests a twelve-second session and records the actual negotiated
timeout. The operation deadline is fifteen seconds. The test requires a
forwarded heartbeat and discarded incoming frame for incoming loss, or a dropped
heartbeat and zero suppressed incoming frames for outgoing loss. Both cases
require disconnection near two-thirds of the negotiated timeout. A three-second scheduling allowance
bounds the wall-clock observation; exact deadline logic also has server-free tests.
Outgoing traffic must not keep a silent connection ready.

The proxy heals immediately after disconnection. Authentication and a successful
held watch-reset acknowledgment must precede readiness: new operations remain
`NotSent`, and readiness and the persistent reconnect gap remain pending until
the acknowledgment is released. Recovery must preserve the session ID and
ephemeral owner while advancing the connection generation.

One-shot invalidation, the persistent gap and subsequent real watch delivery,
protected reads, acknowledged close and ephemeral deletion must all succeed.

## Longer interruption

The selected direction stays suppressed across reconnect attempts until the owner reports
local `Lost` near the negotiated timeout, with the same scheduling allowance.
Restoration replies are not artificially held in this case.

Both watch streams and pending readiness must fail with the terminal session
error. After healing, synchronized direct control requests observe three seconds
without further owner connection attempts. The owner must remain `Lost`, keep
its diagnostic session ID and terminal generation, and reject new work as
`NotSent`. Local loss does not establish server-confirmed expiry or immediate
ephemeral deletion; the test never replaces the owner or recreates its lease.

## Healing and evidence

The finally path always restores both directions, releases watch-reset holds,
and disposes the owner while observing its pending create. A fresh explicit probe
then connects through the healed proxy, authenticates, synchronizes and receives
an acknowledged close. It is separate from the tested owner. A deliberate abort
after an outgoing create is discarded checks this cleanup path separately from
passing cases.

The test has a ninety-second guard, the proxy a seventy-five-second lifetime,
and the runner a two-minute process guard. Event reads allow twenty seconds;
cleanup uses an independent fifty-second budget. The runner retains per-direction and per-outcome
configuration, roles, versions, timelines, safe counters, server logs and cleanup
results in the printed artifact directory, then removes its owned fixture and
volumes. Passwords, session credentials, paths and payloads are excluded from
managed diagnostic output.

Local checks cover both directions and outcomes on both pinned versions with a macOS arm64
client. CI selects partition cases independently from server-outage cases;
remote execution remains unobserved. The fixture uses plaintext with the explicit
development digest override.
[Follower peer isolation](verify-peer-partition-recovery.md) has its own check.
Broader network faults, secure connections under this fault and sustained load
require separate evidence.

[How-to guides](README.md) · [Documentation home](../README.md)
