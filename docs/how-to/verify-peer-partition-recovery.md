# Verify recovery from an isolated peer

Run:

```sh
just zk-peer-partition-faults
```

Use the SDK pinned in `global.json`, Python 3, and Docker with Compose supporting
`gw_priority` and `docker network connect --gw-priority`. Host Java and Gradle are
unnecessary; readiness probes use the JDK in each pinned server image.

The command creates separate disposable three-voter ensembles for ZooKeeper
3.8.7 and 3.9.6, with independent leader and follower cases on each version.
Each must report the exact version, one leader and two followers, and pass
authenticated create/read/delete probes. Leader cases select the observed leader;
follower cases select the first observed follower in `zoo1`, `zoo2`, `zoo3` order.
Each role/version pair has its own fixture and artifact names.

## Fault and observations

Client connections and server peer traffic use separate Docker bridges with fixed
addresses. ZooKeeper peer configuration uses literal peer addresses. The client
bridge has higher [gateway priority](https://docs.docker.com/reference/compose-file/services/#gw_priority).
The helper verifies ownership labels and saves container IDs, process IDs, start
times, client interfaces, port mappings and peer addresses before
[disconnecting](https://docs.docker.com/reference/cli/docker/network/disconnect/)
only the selected member's peer interface.

Baseline peer TCP probes must succeed. After detachment, probes must fail in
both directions between the selected peer and majority, while majority peer paths
remain reachable. Follower cases retain the original majority leader. Leader
cases require the two remaining voters to elect a replacement; peer TCP probes
must show a working majority quorum path and a blocked path from the isolated
old leader to its replacement. All three
processes, client interfaces and published mappings must remain unchanged; the
selected client port must initially remain reachable. ZooKeeper may subsequently
close its own client connections or listener after losing quorum.

An independent authenticated majority client must commit a protected write. A
client pinned to the minority before isolation must fail a write with `Unknown`
or `NotSent`; a synchronized majority read must still show the original value
before healing. An already-sent ambiguous write may complete later at the server.
The test does not retry it or infer permanent absence from its failed call.

The owner initially connects through the selected member. One successful
sequential-create reply is withheld, and a majority control confirms the committed
child before isolation. The owner must disconnect, report `Unknown` without an
operation timeout, and reconnect through a majority endpoint with the same
session. A held watch-reset acknowledgment keeps readiness, new requests and the
persistent reconnect gap blocked until restoration completes. Checks then require
retained ephemeral ownership, authentication replay, one-shot and persistent
watch delivery, exactly one sequential child and zero create transmissions through
the recovery endpoints. Acknowledged close must remove the ephemeral.

## Healing and evidence

An independent cleanup budget
[reconnects](https://docs.docker.com/reference/cli/docker/network/connect/)
the original container to its original peer address. Before reconnecting an
isolated old leader, the helper requires replacement election and successful
authenticated create/read/delete probes through both surviving voters. It saves
the elected leader atomically while preserving the original rollback metadata.
Healing verifies unchanged process/client identities, the same serving leader,
full membership and authenticated readiness. The old leader must return as a
follower. Fresh clients through all three members must read the
same separate majority-committed value. A shell exit trap invokes the same guarded
healing helper if the managed test fails, verifies roles after authenticated
readiness, then removes the owned fixture and volumes. Verification waits within
a fixed bound for actual role convergence and fresh authenticated operations on
all three members; cached container health cannot establish recovery.

The case has a ninety-second guard, forwarding endpoints a seventy-five-second
lifetime and the test process a two-minute guard. Docker actions and TCP probes
are bounded; cleanup has an independent fifty-second budget. Metadata, role,
connectivity, outcome, session, watch or recovery mismatches fail the check.

The printed artifact directory retains exact versions, selected roles, Compose
configuration, safe identity metadata, test timelines, server logs and cleanup
results. Diagnostic output excludes credentials, session passwords and payloads.
The fixture uses plaintext with the explicit development digest override.

Leader and follower checks passed on both pinned versions with a macOS arm64
client. A deliberate abort after verified leader isolation exercised managed
healing and authenticated readiness. A second leader probe exited the owned test
process before managed cleanup, requiring the shell fallback to wait for a
replacement majority, heal the detached old leader as a follower and remove the
fixture. Earlier follower probes exercised both cleanup paths as well. These
expected failures are recorded separately from passing cases.

The [CI workflow](../../.github/workflows/build.yml) selects this command separately
from server stops and one-way client-frame loss. Remote execution remains
unobserved. Asymmetric peer traffic loss, simultaneous network faults, host firewall faults,
observers, quorum TLS and sustained load require separate evidence.

[How-to guides](README.md) · [Documentation home](../README.md)
