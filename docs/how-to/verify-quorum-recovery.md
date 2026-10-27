# Verify recovery after quorum loss

Run:

```sh
just zk-quorum-faults
```

Use the SDK pinned in `global.json`, Docker with Compose and Python 3. Host Java
and Gradle are unnecessary; readiness tools run inside the pinned images.

The command runs brief and longer interruptions on ZooKeeper 3.8.7 and 3.9.6.
Each case gets a fresh, labeled three-voter ensemble with loopback ports and a
unique authenticated namespace. Published loopback ports remain fixed across
container restarts and are checked after quorum recovery. A dedicated overlay
uses fixed internal voter addresses and literal peer configuration, keeping this
server-outage check independent of Docker/Java DNS changes. Before each fault, all
servers must report the
exact version, pass authenticated create/read/delete probes and report one
leader and two followers.

The client connects through the observed leader. The controller rechecks the
ownership labels and roles of that leader and the first observed follower, then
stops both together. It verifies that only one voter remains running. This
extends the [single-member checks](verify-leader-recovery.md).

## Brief interruption

The client requests a thirty-second session. After observing disconnection and
closed admission, the test starts the stopped follower first. A serving leader
and follower must pass authenticated readiness within a fifteen-second guard;
the old leader is then started. This controlled healing sequence avoids a
competing election among three simultaneously starting voters.

The test holds a successful watch-reset acknowledgment and checks that readiness
and the persistent reconnect gap remain pending, while new requests are
`NotSent`. The brief restoration observation allows thirty-five seconds, covering
the negotiated recovery budget; the driver retains its own session deadlines.
Releasing the acknowledgment must preserve the session ID and
ephemeral owner, advance the connection generation and deliver a persistent
reconnect gap. Once all three members are healthy, an independent control session
mutates the protected nodes to verify one-shot invalidation and subsequent real
persistent-watch delivery. Acknowledged close must remove the ephemeral.

## Longer interruption

The client requests an eight-second session and records the actual negotiated
timeout, which must fit within the fifteen-second event guard. The servers stay
down until the driver reports local `Lost`; no fixed sleep determines the outage.
Recovery proxies forward restoration replies immediately in this case.

Both watch streams and pending readiness must fail with the terminal session
error. The test then restores quorum and performs synchronized control reads.
The original client must remain `Lost`, retain its diagnostic session ID and
terminal generation, reject new operations as `NotSent`, and make no further
connection attempts during the observed recovery interval.

Local `Lost` means the driver abandoned the session. It does not establish
server-confirmed expiry or immediate ephemeral deletion. Applications must
explicitly create another client if they need a new session.

## Request outcomes and cleanup

Both cases withhold one successful sequential-create reply while leaving its
connection open. A synchronized control read proves commitment before the fault;
the real server stop then causes the call to return `Unknown`. After recovery,
the child must exist exactly once. Only one create may cross the original proxy,
and none may cross either recovery proxy. These controls do not replay requests.

The tested owner uses the measured proxies. After all three members recover,
the test explicitly opens fresh independent control and cleanup sessions for
post-recovery observations. It does not retry an uncertain control mutation or
replace the tested owner. Separating majority availability from full restoration
keeps a late-joining follower from extending the deliberate acknowledgment hold.

Rollback uses the same majority-first healing sequence. Both stopped members must
be restored, all three must report one leader and two followers, and all must pass authenticated readiness. Full restoration
has a forty-second guard inside an independent fifty-second cleanup budget.
The shell exit trap provides another start/wait/log/teardown path. Cleanup removes
only the owned fixture's containers, network and volumes.

Local checks passed for both outcomes on both pinned versions with a macOS arm64
client. Development failures are retained separately from passing checks. A deliberate
abort also verifies rollback without counting as a passing recovery case.

The printed artifact directory retains per-version and per-outage selection,
versions, configuration, initial/final roles, test timelines, server logs and
cleanup results. Credentials, paths, session passwords and payloads are excluded
from managed test output. CI includes a separate quorum job; remote execution
requires its own observed evidence.

The fixture uses plaintext with the development digest override.
[One-way client-traffic loss](verify-partition-recovery.md) and
[follower peer isolation](verify-peer-partition-recovery.md) have separate checks.
Other network faults, changing peer addresses, simultaneous multi-member
startup, observer endpoints, secure outages and sustained faults remain separate checks. See the
[Apache quorum description](https://zookeeper.apache.org/doc/r3.9.6/zookeeperInternals.html#sc_quorums)
and [session documentation](https://zookeeper.apache.org/doc/r3.9.6/zookeeperProgrammers.html#ch_zkSessions).

[How-to guides](README.md) · [Documentation home](../README.md)
