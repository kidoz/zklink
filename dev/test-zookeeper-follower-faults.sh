#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")/.."
if [[ $# -ne 0 ]]; then echo "Usage: $0" >&2; exit 2; fi
for command in docker dotnet python3; do command -v "${command}" >/dev/null; done
docker info >/dev/null
docker compose version >/dev/null
dotnet restore tests/ZkLink.FaultTests --locked-mode
dotnet build tests/ZkLink.FaultTests -c Release --no-restore -m:1
artifact_root=${ZKLINK_MATRIX_ARTIFACT_ROOT:-${TMPDIR:-/tmp}}
mkdir -p "${artifact_root}"
artifacts=$(mktemp -d "${artifact_root%/}/zklink-follower-faults.XXXXXX")
echo "Follower fault artifacts: ${artifacts}"
docker version >"${artifacts}/docker-version.txt"
docker compose version >"${artifacts}/compose-version.txt"
dotnet --info >"${artifacts}/dotnet-info.txt"
git rev-parse HEAD >"${artifacts}/revision.txt"
git status --short >"${artifacts}/working-tree.txt"
unset ZOOKEEPER_IMAGE
export ZOOKEEPER_PORT_1=0 ZOOKEEPER_PORT_2=0 ZOOKEEPER_PORT_3=0
export ZKLINK_FAULT_REPO_ROOT="$PWD"
readonly startup_guard_seconds=90 rollback_guard_seconds=60 runner_guard=2m
project=""
target=""
compose() {
  docker compose -f compose.yaml -f compose.fault.yaml --env-file "dev/zookeeper-${version}.env" --project-name "${project}" "$@"
}
cleanup() {
  local status=$?
  trap - EXIT
  if [[ -n "${project}" ]]; then
    # A second rollback path survives a crashed or cancelled test process.
    if [[ -n "${target}" ]]; then
      compose start "${target}" >>"${artifacts}/${version}-rollback.log" 2>&1 || status=1
      compose up -d --wait --wait-timeout "${rollback_guard_seconds}" >>"${artifacts}/${version}-rollback.log" 2>&1 || status=1
    fi
    compose logs --no-color >>"${artifacts}/${version}-server.log" 2>&1 || status=1
    compose down --volumes >>"${artifacts}/${version}-cleanup.log" 2>&1 || status=1
  fi
  exit "${status}"
}
trap cleanup EXIT
trap 'exit 130' INT
trap 'exit 143' TERM
for version in 3.8.7 3.9.6; do
  export ZKLINK_FAULT_RUN_ID="$(python3 -c 'import uuid; print(uuid.uuid4().hex)')"
  project="zklink-follower-fault-${version//./-}-${ZKLINK_FAULT_RUN_ID}"
  target=""
  echo "Checking follower outage on ZooKeeper ${version}"
  compose config >"${artifacts}/${version}-compose.yaml"
  compose up -d --wait --wait-timeout "${startup_guard_seconds}" >"${artifacts}/${version}-startup.log" 2>&1
  leaders=0
  followers=0
  for service in zoo1 zoo2 zoo3; do
    compose exec -T "${service}" zkServer.sh version >"${artifacts}/${version}-${service}-version.txt" 2>&1
    grep -Fq "Apache ZooKeeper, version ${version} " "${artifacts}/${version}-${service}-version.txt"
    compose exec -T "${service}" bash /opt/zklink/zookeeper-healthcheck.sh
    compose exec -T "${service}" zkServer.sh status >"${artifacts}/${version}-${service}-role-before.txt" 2>&1
    if grep -Fxq 'Mode: follower' "${artifacts}/${version}-${service}-role-before.txt"; then
      followers=$((followers + 1))
      if [[ -z "${target}" ]]; then target="${service}"; fi
    elif grep -Fxq 'Mode: leader' "${artifacts}/${version}-${service}-role-before.txt"; then
      leaders=$((leaders + 1))
    else
      echo "Unrecognized initial role; aborting ${version}" >&2; exit 1
    fi
  done
  if [[ "${leaders}" -ne 1 || "${followers}" -ne 2 ]]; then echo "No healthy voting ensemble" >&2; exit 1; fi
  printf 'Selection: first observed follower in zoo1/zoo2/zoo3 order\nTarget: %s\n' "${target}" >"${artifacts}/${version}-selection.txt"
  endpoints="$(compose port zoo1 2181),$(compose port zoo2 2181),$(compose port zoo3 2181)"
  ZKLINK_TEST_CONNECTION_STRING="${endpoints}" ZKLINK_TEST_SERVER_VERSION="${version}" \
    ZKLINK_FAULT_PROJECT="${project}" ZKLINK_FAULT_SERVICE="${target}" ZKLINK_FAULT_ENDPOINT="$(compose port "${target}" 2181)" \
    dotnet test --project tests/ZkLink.FaultTests -c Release --no-build --no-restore \
    --output Detailed --show-stdout All --show-live-output on --timeout "${runner_guard}" \
    --results-directory "${artifacts}/${version}-results" >"${artifacts}/${version}-tests.txt" 2>&1 || {
      cat "${artifacts}/${version}-tests.txt"; exit 1;
    }
  cat "${artifacts}/${version}-tests.txt"
  for service in zoo1 zoo2 zoo3; do
    compose exec -T "${service}" bash /opt/zklink/zookeeper-healthcheck.sh
    compose exec -T "${service}" zkServer.sh status >"${artifacts}/${version}-${service}-role-after.txt" 2>&1
  done
  compose ps --format json >"${artifacts}/${version}-status.json"
  compose logs --no-color >"${artifacts}/${version}-server.log" 2>&1
  compose down --volumes >"${artifacts}/${version}-cleanup.log" 2>&1
  project=""
  target=""
  echo "ZooKeeper ${version}: PASS (follower stop, same-session recovery, no replay, restoration, rollback and cleanup)"
done
