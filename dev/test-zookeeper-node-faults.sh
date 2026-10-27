#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")/.."
if [[ $# -ne 1 || ( "$1" != follower && "$1" != leader && "$1" != quorum && "$1" != partition && "$1" != peer && "$1" != pressure && "$1" != soak ) ]]; then
  echo "Usage: $0 follower|leader|quorum|partition|peer|pressure|soak" >&2; exit 2
fi
role="$1"
export ZKLINK_FAULT_ROLE="${role}"
for command in docker dotnet python3; do command -v "${command}" >/dev/null; done
runner_guard=2m
if [[ "${role}" == soak ]]; then
  export ZKLINK_SOAK_DURATION_SECONDS="${ZKLINK_SOAK_DURATION_SECONDS-120}"
  export ZKLINK_SOAK_SEED="${ZKLINK_SOAK_SEED-1729}"
  read -r ZKLINK_SOAK_DURATION_SECONDS ZKLINK_SOAK_SEED runner_guard < <(python3 - <<'SETTINGS'
import os
import re
DURATION_MIN_SECONDS = 30
DURATION_MAX_SECONDS = 86400
SEED_MAX = 2147483647
PROCESS_OVERHEAD_SECONDS = 120
raw_duration = os.environ["ZKLINK_SOAK_DURATION_SECONDS"]
raw_seed = os.environ["ZKLINK_SOAK_SEED"]
if not re.fullmatch(r"[0-9]{1,5}", raw_duration) or not re.fullmatch(r"[0-9]{1,10}", raw_seed):
    raise SystemExit("Soak duration and seed must be decimal integers.")
duration, seed = int(raw_duration), int(raw_seed)
if not DURATION_MIN_SECONDS <= duration <= DURATION_MAX_SECONDS or not 0 <= seed <= SEED_MAX:
    raise SystemExit("Soak duration must be 30..86400 seconds; seed must be 0..2147483647.")
print(duration, seed, str(duration + PROCESS_OVERHEAD_SECONDS) + "s")
SETTINGS
)
  export ZKLINK_SOAK_DURATION_SECONDS ZKLINK_SOAK_SEED
fi
readonly runner_guard
docker info >/dev/null
docker compose version >/dev/null
dotnet restore tests/ZkLink.FaultTests --locked-mode
dotnet build tests/ZkLink.FaultTests -c Release --no-restore -m:1
artifact_root=${ZKLINK_MATRIX_ARTIFACT_ROOT:-${TMPDIR:-/tmp}}
mkdir -p "${artifact_root}"
artifacts=$(mktemp -d "${artifact_root%/}/zklink-${role}-faults.XXXXXX")
echo "Member fault artifacts: ${artifacts}"
docker version >"${artifacts}/docker-version.txt"
docker compose version >"${artifacts}/compose-version.txt"
dotnet --info >"${artifacts}/dotnet-info.txt"
git rev-parse HEAD >"${artifacts}/revision.txt"
git status --short >"${artifacts}/working-tree.txt"
unset ZOOKEEPER_IMAGE
export ZOOKEEPER_PORT_1=0 ZOOKEEPER_PORT_2=0 ZOOKEEPER_PORT_3=0
export ZKLINK_FAULT_REPO_ROOT="$PWD"
readonly startup_guard_seconds=90 rollback_guard_seconds=60
project=""
target=""
allocation_network=""
compose_files=(-f compose.yaml -f compose.fault.yaml)
if [[ "${role}" == quorum ]]; then compose_files+=(-f compose.quorum-fault.yaml); fi
if [[ "${role}" == peer ]]; then compose_files+=(-f compose.peer-fault.yaml); fi
compose() {
  docker compose "${compose_files[@]}" --env-file "dev/zookeeper-${version}.env" --project-name "${project}" "$@"
}
cleanup() {
  local cleanup_exit=$?
  trap - EXIT
  if [[ -n "${allocation_network}" ]]; then
    allocation_label=$(docker network inspect --format '{{index .Labels "org.zklink.fault-run"}}' "${allocation_network}" 2>/dev/null || true)
    if [[ "${allocation_label}" == "${ZKLINK_FAULT_RUN_ID}" ]]; then
      docker network rm "${allocation_network}" >>"${artifacts}/${evidence_key}-cleanup.log" 2>&1 || cleanup_exit=1
    fi
  fi
  if [[ -n "${project}" ]]; then
    # A second rollback path survives a crashed or cancelled test process.
    if [[ -n "${target}" ]]; then
      if [[ "${role}" == peer && -f "${ZKLINK_PEER_STATE_FILE}" ]]; then
        ZKLINK_FAULT_PROJECT="${project}" ZKLINK_FAULT_SERVICE="${target}" \
          python3 dev/zookeeper-peer-network.py heal >>"${artifacts}/${evidence_key}-rollback.log" 2>&1 || cleanup_exit=1
      fi
      compose start >>"${artifacts}/${evidence_key}-rollback.log" 2>&1 || cleanup_exit=1
      compose up -d --wait --wait-timeout "${rollback_guard_seconds}" >>"${artifacts}/${evidence_key}-rollback.log" 2>&1 || cleanup_exit=1
      if [[ "${role}" == peer && -f "${ZKLINK_PEER_STATE_FILE}" ]]; then
        ZKLINK_FAULT_PROJECT="${project}" ZKLINK_FAULT_SERVICE="${target}" \
          python3 dev/zookeeper-peer-network.py verify >>"${artifacts}/${evidence_key}-rollback.log" 2>&1 || cleanup_exit=1
      fi
      if [[ "${role}" == pressure || "${role}" == soak ]]; then
        for service in zoo1 zoo2 zoo3; do
          if compose exec -T "${service}" bash /opt/zklink/zookeeper-healthcheck.sh >>"${artifacts}/${evidence_key}-rollback.log" 2>&1; then
            printf '%s-cleanup-authenticated-ready: %s\n' "${role}" "${service}" >>"${artifacts}/${evidence_key}-rollback.log"
          else
            cleanup_exit=1
          fi
        done
      fi
    fi
    compose logs --no-color >>"${artifacts}/${evidence_key}-server.log" 2>&1 || cleanup_exit=1
    compose down --volumes >>"${artifacts}/${evidence_key}-cleanup.log" 2>&1 || cleanup_exit=1
  fi
  exit "${cleanup_exit}"
}
trap cleanup EXIT
trap 'exit 130' INT
trap 'exit 143' TERM
outages=(member)
test_fixture=MemberFault
if [[ "${role}" == partition ]]; then test_fixture=PartitionFault; fi
if [[ "${role}" == pressure ]]; then test_fixture=WatchPressure; fi
if [[ "${role}" == soak ]]; then test_fixture=Soak; fi
if [[ "${role}" == quorum || "${role}" == partition ]]; then outages=(brief lost); fi
peer_roles=(none)
if [[ "${role}" == peer ]]; then peer_roles=(leader follower); fi
directions=(none)
if [[ "${role}" == partition ]]; then directions=(outgoing incoming); fi
for peer_role in "${peer_roles[@]}"; do
export ZKLINK_PEER_TARGET_ROLE="${peer_role}"
for direction in "${directions[@]}"; do
export ZKLINK_PARTITION_DIRECTION="${direction}"
for outage in "${outages[@]}"; do
  for version in 3.8.7 3.9.6; do
    evidence_key="${version}"
    if [[ "${role}" == quorum || "${role}" == partition ]]; then evidence_key="${version}-${outage}"; fi
    if [[ "${role}" == peer ]]; then evidence_key="${version}-${peer_role}"; fi
    if [[ "${role}" == partition ]]; then evidence_key="${version}-${direction}-${outage}"; fi
    export ZKLINK_FAULT_RUN_ID="$(python3 -c 'import uuid; print(uuid.uuid4().hex)')"
    project="zklink-${role}-fault-${version//./-}-${ZKLINK_FAULT_RUN_ID}"
    target=""
    second_target=""
    export ZKLINK_FAULT_OUTAGE="${outage}"
    export ZKLINK_PEER_STATE_FILE="${artifacts}/${evidence_key}-peer-state.json"
    export ZKLINK_SOAK_RESULT_FILE="${artifacts}/${evidence_key}-soak.jsonl"
    if [[ "${role}" == soak ]]; then
      printf 'DurationSeconds: %s\nSeed: %s\n' "${ZKLINK_SOAK_DURATION_SECONDS}" "${ZKLINK_SOAK_SEED}" >"${artifacts}/${evidence_key}-soak-settings.txt"
    fi
    # Docker can reassign a zero-published host port when its container restarts.
    # Bind candidates together to choose distinct available loopback ports. If
    # another process wins the subsequent bind race, Compose startup must fail.
    if [[ "${role}" == quorum || "${role}" == peer ]]; then
      allocation_network="${project}-allocation"
      read -r ZKLINK_FAULT_SUBNET ZKLINK_FAULT_IP_1 ZKLINK_FAULT_IP_2 ZKLINK_FAULT_IP_3 ZKLINK_FAULT_CLIENT_SUBNET ZKLINK_FAULT_CLIENT_IP_1 ZKLINK_FAULT_CLIENT_IP_2 ZKLINK_FAULT_CLIENT_IP_3 < <(python3 - "${allocation_network}" "${ZKLINK_FAULT_RUN_ID}" "${role}" <<'NETWORK'
import ipaddress
import subprocess
import sys
name, run_id, role = sys.argv[1:]
COMMAND_TIMEOUT_SECONDS = 10
FIRST_MEMBER_HOST = 11
def docker(*args):
    return subprocess.check_output(["docker", *args], text=True, timeout=COMMAND_TIMEOUT_SECONDS).strip()
created = False
try:
    docker("network", "create", "--label", "org.zklink.fault-run=" + run_id, name)
    created = True
    subnet = ipaddress.ip_network(docker("network", "inspect", "--format", "{{(index .IPAM.Config 0).Subnet}}", name))
    assert subnet.version == 4 and subnet.num_addresses > FIRST_MEMBER_HOST + 3
    client = None
    if role == "peer":
        client, subnet = list(subnet.subnets(prefixlen_diff=1))
    addresses = [str(subnet[FIRST_MEMBER_HOST + member]) for member in range(3)]
finally:
    if created:
        assert docker("network", "inspect", "--format", '{{index .Labels "org.zklink.fault-run"}}', name) == run_id
        docker("network", "rm", name)
extra = [str(client), *(str(client[FIRST_MEMBER_HOST + member]) for member in range(3))] if client else []
print(str(subnet), *addresses, *extra)
NETWORK
)
      allocation_network=""
      export ZKLINK_FAULT_SUBNET ZKLINK_FAULT_IP_1 ZKLINK_FAULT_IP_2 ZKLINK_FAULT_IP_3 ZKLINK_FAULT_CLIENT_SUBNET ZKLINK_FAULT_CLIENT_IP_1 ZKLINK_FAULT_CLIENT_IP_2 ZKLINK_FAULT_CLIENT_IP_3
      read -r ZOOKEEPER_PORT_1 ZOOKEEPER_PORT_2 ZOOKEEPER_PORT_3 < <(python3 - <<'PORTS'
import socket
with socket.socket() as first, socket.socket() as second, socket.socket() as third:
    sockets = [first, second, third]
    for listener in sockets:
        listener.bind(("127.0.0.1", 0))
    print(*(listener.getsockname()[1] for listener in sockets))
PORTS
)
    fi
    case_label="${outage}"
    if [[ "${role}" == peer ]]; then case_label="${peer_role}"; fi
    if [[ "${role}" == partition ]]; then case_label="${direction}/${outage}"; fi
    if [[ "${role}" == soak ]]; then
      echo "Checking seeded soak on ZooKeeper ${version}: seconds=${ZKLINK_SOAK_DURATION_SECONDS} seed=${ZKLINK_SOAK_SEED}"
    elif [[ "${role}" == pressure ]]; then
      echo "Checking watch pressure on ZooKeeper ${version}"
    else
      echo "Checking ${role} outage (${case_label}) on ZooKeeper ${version}"
    fi
    compose config >"${artifacts}/${evidence_key}-compose.yaml"
    compose up -d --wait --wait-timeout "${startup_guard_seconds}" >"${artifacts}/${evidence_key}-startup.log" 2>&1
    leaders=0
    followers=0
    for service in zoo1 zoo2 zoo3; do
      compose exec -T "${service}" zkServer.sh version >"${artifacts}/${evidence_key}-${service}-version.txt" 2>&1
      grep -Fq "Apache ZooKeeper, version ${version} " "${artifacts}/${evidence_key}-${service}-version.txt"
      compose exec -T "${service}" bash /opt/zklink/zookeeper-healthcheck.sh
      compose exec -T "${service}" zkServer.sh status >"${artifacts}/${evidence_key}-${service}-role-before.txt" 2>&1
      if grep -Fxq 'Mode: follower' "${artifacts}/${evidence_key}-${service}-role-before.txt"; then
        followers=$((followers + 1))
        if [[ "${role}" == quorum && -z "${second_target}" ]]; then second_target="${service}"; fi
        if [[ ( "${role}" == follower || "${role}" == pressure || "${role}" == soak || ( "${role}" == peer && "${peer_role}" == follower ) ) && -z "${target}" ]]; then target="${service}"; fi
      elif grep -Fxq 'Mode: leader' "${artifacts}/${evidence_key}-${service}-role-before.txt"; then
        leaders=$((leaders + 1))
        if [[ ( "${role}" == leader || "${role}" == quorum || "${role}" == partition || ( "${role}" == peer && "${peer_role}" == leader ) ) && -z "${target}" ]]; then target="${service}"; fi
      else
        echo "Unrecognized initial role; aborting ${version}" >&2; exit 1
      fi
    done
    if [[ "${leaders}" -ne 1 || "${followers}" -ne 2 ]]; then echo "No healthy voting ensemble" >&2; exit 1; fi
    selection="first observed ${role}"
    if [[ "${role}" == peer ]]; then selection="observed ${peer_role} for peer isolation"; fi
    if [[ "${role}" == pressure || "${role}" == soak ]]; then selection="first observed follower as transparent forwarding target"; fi
    if [[ "${role}" == partition ]]; then selection="observed leader as the client proxy target"; fi
    if [[ "${role}" == quorum ]]; then selection="observed leader and first observed follower"; fi
    printf 'Selection: %s in zoo1/zoo2/zoo3 order\nTarget: %s\n' "${selection}" "${target}" >"${artifacts}/${evidence_key}-selection.txt"
    if [[ "${role}" == partition ]]; then printf 'Direction: %s\nOutage: %s\n' "${direction}" "${outage}" >>"${artifacts}/${evidence_key}-selection.txt"; fi
    if [[ "${role}" == quorum ]]; then printf 'Second stopped member: %s\nOutage: %s\n' "${second_target}" "${outage}" >>"${artifacts}/${evidence_key}-selection.txt"; fi
    endpoints="$(compose port zoo1 2181),$(compose port zoo2 2181),$(compose port zoo3 2181)"
    ZKLINK_TEST_CONNECTION_STRING="${endpoints}" ZKLINK_TEST_SERVER_VERSION="${version}" \
      ZKLINK_FAULT_SECOND_SERVICE="${second_target}" ZKLINK_FAULT_PROJECT="${project}" ZKLINK_FAULT_SERVICE="${target}" ZKLINK_FAULT_ENDPOINT="$(compose port "${target}" 2181)" \
      dotnet test --project tests/ZkLink.FaultTests -c Release --no-build --no-restore \
      --filter-trait "Fixture=${test_fixture}" --output Detailed --show-stdout All --show-live-output on --timeout "${runner_guard}" \
      --results-directory "${artifacts}/${evidence_key}-results" >"${artifacts}/${evidence_key}-tests.txt" 2>&1 || {
        cat "${artifacts}/${evidence_key}-tests.txt"; exit 1;
      }
    cat "${artifacts}/${evidence_key}-tests.txt"
    for service in zoo1 zoo2 zoo3; do
      compose exec -T "${service}" bash /opt/zklink/zookeeper-healthcheck.sh
      compose exec -T "${service}" zkServer.sh status >"${artifacts}/${evidence_key}-${service}-role-after.txt" 2>&1
    done
    compose ps --format json >"${artifacts}/${evidence_key}-status.json"
    compose logs --no-color >"${artifacts}/${evidence_key}-server.log" 2>&1
    compose down --volumes >"${artifacts}/${evidence_key}-cleanup.log" 2>&1
    project=""
    target=""
    if [[ "${role}" == soak ]]; then
      echo "ZooKeeper ${version}: PASS (seeded churn, CRUD, cancellation, watch/session recovery, resource checkpoints and cleanup)"
    elif [[ "${role}" == pressure ]]; then
      echo "ZooKeeper ${version}: PASS (slow consumers, heartbeat progress, shared event overflow, FIFO, counter and fixture cleanup)"
    else
      echo "ZooKeeper ${version}: PASS (${role}/${case_label} fault, expected session outcome, no replay, restoration, rollback and cleanup)"
    fi
  done
done
done
done
