# Elect a leader with predecessor watches

This runnable example gives three participants separate ZooKeeper sessions. Each
creates an ephemeral sequential node under a unique, persistent election parent.
The participant with the lowest sequence wins; each follower watches only the
candidate immediately before it. Closing the leader's session removes its node
and lets the next participant take its turn.

## Run the example

Use the pinned .NET SDK, Docker with Compose, `just` and OpenSSL. From the
repository root:

```sh
just zk-up 3.9.6
export ZKLINK_USER=zklink-election-example
export ZKLINK_PASSWORD="$(openssl rand -hex 24)"
dotnet run --project samples/ZkLink.Samples.Basic -c Release -- \
  127.0.0.1:2181,127.0.0.1:2182,127.0.0.1:2183 --plaintext --election
unset ZKLINK_USER ZKLINK_PASSWORD
just zk-down 3.9.6
```

Expect:

```text
Leader election: three separate sessions, predecessor watches, ordered handoff and ephemeral cleanup verified.
```

The final session output also includes `Close: Acknowledged`. The three
participants share a disposable digest identity so the creator ACL lets each
read the others' nodes. The coordinator owns namespace cleanup. `--plaintext`
selects the local fixture explicitly; the normal client default is TLS.

## Follow the algorithm

[ElectionCandidate.cs](../../samples/ZkLink.Samples.Basic/ElectionCandidate.cs)
contains the algorithm; [ElectionSample.cs](../../samples/ZkLink.Samples.Basic/ElectionSample.cs)
connects the participants and verifies the handoffs.

1. Create `candidate-` using `CreateMode.EphemeralSequential` and keep the actual
   path returned by `CreateAsync`. Each participant creates exactly once.
2. List the parent's children without a child watch. Sort the fixed-prefix,
   ten-digit sequence names with ordinal comparison, and verify the participant's
   node still exists with its original creation zxid and session owner.
3. If the participant is first, the election observation succeeds. Otherwise call
   `ExistsAndWatchAsync` on its immediate predecessor.
4. If the predecessor is already missing, dispose the returned existence watch
   and relist immediately. This handles deletion between listing and registration.
5. If it exists, await one invalidation, dispose the watch and relist. A data
   change also consumes this one-shot watch, so it must trigger the same recheck.
6. After the first participant's acknowledged close, verify the second wins while
   the third still waits. Close the second, verify the third wins, then close it
   and verify the parent is empty before removing it.

This follows Apache's [leader election and predecessor-watch recipes](https://zookeeper.apache.org/doc/r3.9.6/recipes.html#sc_leaderElection).
Watching a single predecessor avoids waking every follower on each departure.

## Failure and ownership boundaries

The sample is a bounded demonstration of election and orderly handoff. It does
not run a protected workload or return a durable leadership lease. A successful
observation can become stale immediately. A production application needs an
ownership-loss signal, immediate suspension of work on disconnect, reconciliation
after recovery, and fencing enforced by the protected resource. A sequence suffix
is not a globally safe fencing token, especially if the parent is recreated.

The sample rejects a disconnected session or a changed connection generation at
its observation boundaries. It does not resume an old election after reconnect.
A waiting watch can remain pending until a notification, terminal error, or the
sample's 30-second overall deadline; it is not a disconnect notification API.

Creates are never retried: an `Unknown` outcome may mean the node was created
without its reply being received. A production retry policy needs protected
creation and reconciliation of a unique attempt identifier. On failure or
cancellation, this example disposes participant sessions and uses a separate
10-second cleanup deadline to delete any remaining nodes under its private parent.
Cleanup errors fail the run. Without acknowledged close or explicit deletion,
ephemeral removal may wait for server-side session expiry.

Only three candidates join a fresh parent. Unexpected names and signed sequence
rollover are rejected. The example does not cover crash failover, network
partitions, production retry policies, or release qualification of a recipes
package.

## Test in Docker

```sh
just zk-operations-matrix
```

The existing runner starts isolated three-node ensembles for the pinned ZooKeeper
3.8.7 and 3.9.6 images. It runs the election sample on every node, together with
CRUD, transaction, auxiliary and watch examples. The live integration suite tests
ordered handoff, immediate-predecessor selection, deletion before registration,
data-change invalidation, cancellation, missing candidate nodes and rejection of
old election observations after reconnect. Tests compile the same algorithm source
as the sample. The runner retains logs and removes its own containers and volumes
on success or failure.

[Tutorials](README.md) · [Documentation home](../README.md)
