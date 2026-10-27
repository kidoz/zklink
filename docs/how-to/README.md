# How-to guides

Complete a specific task in an existing checkout.

- [Run the development checks](run-development-checks.md) — check formatting,
  dependencies, public contracts, and protocol bytes after a change.
- [Run a local ZooKeeper ensemble](run-local-zookeeper.md) — start a pinned
  three-node fixture, collect logs, and remove its disposable data.
- [Verify incoming-traffic recovery](verify-partition-recovery.md) — block incoming
  client traffic while forwarding heartbeats and check recovery or terminal loss.
- [Verify quorum recovery](verify-quorum-recovery.md) — stop two voters and check
  valid-session recovery or terminal local loss, with verified cleanup.
- [Verify leader recovery](verify-leader-recovery.md) — stop the observed leader,
  check replacement election and session/watch recovery, and restore it as a follower.
- [Verify follower recovery](verify-follower-recovery.md) — stop one observed
  follower, check session/watch recovery and request outcomes, and restore it.
- [Verify secure connections](verify-secure-connections.md) — run isolated TLS
  and mTLS fixtures, certificate rejection and secure recovery checks.
- [Register ZkLink with dependency injection](use-dependency-injection.md) — let
  a Microsoft host connect, share and close one ZooKeeper client.

For a first walkthrough, use the [build tutorial](../tutorials/build-and-test.md).

[Documentation home](../README.md)
