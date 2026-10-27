# Run a local ZooKeeper ensemble

Use the pinned Compose fixture to obtain a disposable three-node ensemble.
Install Docker with Compose and `just`, start Docker, and run commands from the
repository root. The fixture uses Java readiness probes. The
[connection sample](../tutorials/connect-a-session.md) connects with ZkLink.

## Start and inspect an ensemble

For the default fixture, ZooKeeper 3.9.6:

```sh
just zk-check 3.9.6
just zk-up 3.9.6
just zk-status 3.9.6
```

Startup waits for all three nodes to become healthy. Use the same version on
every lifecycle command. To use 3.8.7, replace `3.9.6` with `3.8.7` throughout.

| Version | Host endpoints |
| --- | --- |
| 3.9.6 | `127.0.0.1:2181`, `127.0.0.1:2182`, `127.0.0.1:2183` |
| 3.8.7 | `127.0.0.1:22181`, `127.0.0.1:22182`, `127.0.0.1:22183` |

The ports bind to loopback and use plaintext. TLS/mTLS fixtures are available separately through
[Verify secure connections](verify-secure-connections.md); authentication-required
server fixtures remain open. Image pins and environment variables are listed in
the [development reference](../reference/development.md#zookeeper-fixture-settings).

## Preserve logs and remove the fixture

Collect logs before cleanup, including after a failed startup:

```sh
just zk-logs 3.9.6 > zklink-zookeeper-3.9.6.log
just zk-down 3.9.6
```

Keep collected logs outside any proposed commit. `zk-down` removes the selected
ensemble and its data volumes; use it only when that fixture's data is disposable.

Without `just`, the equivalent lifecycle commands are:

```sh
docker compose --env-file dev/zookeeper-3.9.6.env config --quiet
docker compose --env-file dev/zookeeper-3.9.6.env up -d --wait --wait-timeout 90
docker compose --env-file dev/zookeeper-3.9.6.env ps
docker compose --env-file dev/zookeeper-3.9.6.env logs --no-color
docker compose --env-file dev/zookeeper-3.9.6.env down --volumes
```

## Smoke-test both pinned versions

On macOS or Linux with Bash available, run:

```sh
just zk-matrix
```

Require a `PASS` result for each version and a successful exit. The runner uses
unique Compose projects and dynamic loopback ports, retains artifacts in the
temporary directory it prints, and attempts cleanup on success or failure. If
cleanup fails, inspect the printed project's cleanup log and remove that project's
containers and volumes using its recorded configuration. The command fails if a
required prerequisite or check fails.

To additionally run the managed driver's new-session, digest-authentication,
sync and acknowledged-close checks on every node, use `just zk-driver-matrix`.
It requires the pinned .NET SDK and OpenSSL, and retains per-node driver output.
Use `just zk-operations-matrix` for the separate live node/ACL/transaction/auxiliary/watch suite and examples. Use `just zk-security-matrix` for dedicated client TLS/mTLS checks. Broader
fault recovery remains unqualified.

## Resolve common startup problems

| Symptom | Action |
| --- | --- |
| Docker daemon unavailable | Start Docker and check `docker info`. |
| Port already allocated | Stop the conflicting fixture, or select unused ports as below. |
| Image pull fails | Check registry access and the selected environment file; retain the checked-in image pin for reproducible checks. |
| Startup times out or a node is unhealthy | Save logs and inspect `just zk-status 3.9.6` before cleanup. |

For a concurrent manual fixture, set a unique project name and three unused ports
in the same shell before running the lifecycle commands:

```sh
export COMPOSE_PROJECT_NAME=zklink-local-extra
export ZOOKEEPER_PORT_1=32181
export ZOOKEEPER_PORT_2=32182
export ZOOKEEPER_PORT_3=32183
```

Keep those settings for logs and cleanup. After `zk-down`, unset them:

```sh
unset COMPOSE_PROJECT_NAME ZOOKEEPER_PORT_1 ZOOKEEPER_PORT_2 ZOOKEEPER_PORT_3
```

See [What the verification layers prove](../explanation/verification.md) for why
successful fixture checks do not establish driver compatibility.

For TTL creation, run `just zk-ttl-matrix` for enabled and disabled fixtures, or
follow the manual override commands in [TTL node creation](../reference/ttl-nodes.md).

[How-to guides](README.md) · [Documentation home](../README.md)
