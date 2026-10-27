#!/usr/bin/env bash
set -euo pipefail

# Each probe owns an ephemeral node and fresh credentials. Secure fixtures use
# the same authenticated operation through the actual TLS client port.
probe_endpoint=127.0.0.1:2181
if [[ "${ZKLINK_HEALTH_TLS:-false}" == true ]]; then
  probe_endpoint=localhost:2281
  export CLIENT_JVMFLAGS="-Dzookeeper.client.secure=true -Dzookeeper.clientCnxnSocket=org.apache.zookeeper.ClientCnxnSocketNetty -Dzookeeper.ssl.keyStore.location=/opt/zklink/pki/client.p12 -Dzookeeper.ssl.keyStore.passwordPath=/opt/zklink/pki/store-password -Dzookeeper.ssl.trustStore.location=/opt/zklink/pki/trust.p12 -Dzookeeper.ssl.trustStore.passwordPath=/opt/zklink/pki/store-password"
fi
probe_id=$(cat /proc/sys/kernel/random/uuid)
probe_password=$(cat /proc/sys/kernel/random/uuid)
probe_path="/zklink-health-${probe_id}"
probe_value="zklink-ready-${probe_id}"

# Capture CLI output so credentials, payloads, and session details do not enter health logs.
# Leave delete as the final command so its operation exit status is checked.
if ! output=$(timeout 12s zkCli.sh -server "${probe_endpoint}" -timeout 5000 -waitforconnection 2>&1 <<EOF
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
