#!/usr/bin/env bash
set -euo pipefail

# Disposable plaintext fixture only. Each probe owns an ephemeral node and fresh credentials.
probe_id=$(cat /proc/sys/kernel/random/uuid)
probe_password=$(cat /proc/sys/kernel/random/uuid)
probe_path="/zklink-health-${probe_id}"
probe_value="zklink-ready-${probe_id}"

# Capture CLI output so credentials, payloads, and session details do not enter health logs.
# Leave delete as the final command so its operation exit status is checked.
if ! output=$(timeout 12s zkCli.sh -server 127.0.0.1:2181 -timeout 5000 -waitforconnection 2>&1 <<EOF
addauth digest zklink-health:${probe_password}
create -e ${probe_path} ${probe_value} auth::cdrwa
get ${probe_path}
delete ${probe_path}
EOF
); then
  echo "ZooKeeper authenticated readiness check failed" >&2
  exit 1
fi

# Strip CLI prompts, which may share a line with the read result when stdin is a pipe.
output=$(sed -E 's/\[zk: [^]]*\] //g' <<<"${output}")

# The CLI can return zero without performing a command, so require the actual create/read results.
if ! grep -Fq "Created ${probe_path}" <<<"${output}" ||
   ! grep -Fxq "${probe_value}" <<<"${output}"; then
  echo "ZooKeeper readiness check did not confirm create/read/delete" >&2
  exit 1
fi
