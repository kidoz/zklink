# Verify recovery from incoming traffic loss

Run:

```sh
just zk-partition-faults
```

Use the SDK pinned in `global.json`, Docker with Compose and Python 3. Host Java
and Gradle are unnecessary; readiness tools run inside the pinned server images.

The command runs short and longer interruptions on ZooKeeper 3.8.7 and 3.9.6.
Each outcome/version gets a fresh labeled three-voter fixture and an authenticated
namespace. All servers must report the exact version, pass authenticated
create/read/delete probes and report one leader and two followers before testing.
The tested client connects through a loopback proxy to the observed leader;
an independent client connects directly for synchronized control observations.

## What is blocked

The proxy consumes and discards complete incoming ZooKeeper frames, including
reconnect handshake replies. Outgoing application frames and actual heartbeats
continue to reach the healthy server. TCP control acknowledgments remain active,
and the proxy does not deliberately close the connection when the fault begins.
Discarded frames are zeroed and never queued for delivery after healing.

This is a one-way application-traffic fault. The command does not stop servers,
change firewall rules or partition the links between ZooKeeper voters.

Before enabling silence, one successful sequential-create reply is withheld
without closing its transport. A synchronized control read proves commitment.
The caller must later receive `Unknown` from transport recovery, rather than an
operation timeout. After either outcome, exactly one child must remain and the
proxy must record exactly one create transmission beyond the initial baseline.

## Short interruption

The client requests a twelve-second session and records the actual negotiated
timeout. The operation deadline is fifteen seconds. The test requires at least
one forwarded heartbeat and a discarded incoming frame, followed by disconnection
near two-thirds of the negotiated timeout. A three-second scheduling allowance
bounds the wall-clock observation; exact deadline logic also has server-free tests.
Outgoing traffic must not keep the silent connection ready.

The proxy heals immediately after disconnection. Authentication and a successful
held watch-reset acknowledgment must precede readiness: new operations remain
`NotSent`, and readiness and the persistent reconnect gap remain pending until
the acknowledgment is released. Recovery must preserve the session ID and
ephemeral owner while advancing the connection generation.

One-shot invalidation, the persistent gap and subsequent real watch delivery,
protected reads, acknowledged close and ephemeral deletion must all succeed.

## Longer interruption

Incoming frames stay suppressed across reconnect attempts until the owner reports
local `Lost` near the negotiated timeout, with the same scheduling allowance.
Restoration replies are not artificially held in this case.

Both watch streams and pending readiness must fail with the terminal session
error. After healing, synchronized direct control requests observe three seconds
without further owner connection attempts. The owner must remain `Lost`, keep
its diagnostic session ID and terminal generation, and reject new work as
`NotSent`. Local loss does not establish server-confirmed expiry or immediate
ephemeral deletion; the test never replaces the owner or recreates its lease.

## Healing and evidence

The finally path always restores incoming delivery, releases watch-reset holds,
and disposes the owner while observing its pending create. A fresh explicit probe
then connects through the healed proxy, authenticates, synchronizes and receives
an acknowledged close. It is separate from the tested owner. A deliberate abort
after enabling the fault checks this cleanup path separately from passing cases.

The test has a ninety-second guard, the proxy a seventy-five-second lifetime,
and the runner a two-minute process guard. Event reads allow twenty seconds;
cleanup uses an independent fifty-second budget. The runner retains per-outcome
configuration, roles, versions, timelines, safe counters, server logs and cleanup
results in the printed artifact directory, then removes its owned fixture and
volumes. Passwords, session credentials, paths and payloads are excluded from
managed diagnostic output.

Local checks cover both outcomes on both pinned versions with a macOS arm64
client. CI selects partition cases independently from server-outage cases;
remote execution remains unobserved. The fixture uses plaintext with the explicit
development digest override. Client-to-server loss, kernel/IP faults, inter-server
partitions, secure connections under this fault and sustained load require
separate evidence.

[How-to guides](README.md) · [Documentation home](../README.md)
