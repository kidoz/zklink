#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")/.."

driver=false
operations=false
if [[ $# -eq 1 && "$1" == "--driver" ]]; then
  driver=true
elif [[ $# -eq 1 && "$1" == "--operations" ]]; then
  operations=true
elif [[ $# -ne 0 ]]; then
  echo "Usage: $0 [--driver|--operations]" >&2
  exit 2
fi
if [[ "${operations}" == true ]]; then
  dotnet restore tests/ZkLink.IntegrationTests --locked-mode
  dotnet build tests/ZkLink.IntegrationTests -c Release --no-restore
fi
if [[ "${driver}" == true || "${operations}" == true ]]; then
  dotnet restore samples/ZkLink.Samples.Basic --locked-mode
  dotnet build samples/ZkLink.Samples.Basic -c Release --no-restore
fi

docker compose version >/dev/null
docker info >/dev/null
artifact_root=${ZKLINK_MATRIX_ARTIFACT_ROOT:-${TMPDIR:-/tmp}}
mkdir -p "${artifact_root}"
artifacts=$(mktemp -d "${artifact_root%/}/zklink-zookeeper-matrix.XXXXXX")
echo "ZooKeeper matrix artifacts: ${artifacts}"
docker version >"${artifacts}/docker-version.txt"
docker compose version >"${artifacts}/compose-version.txt"
git rev-parse --verify HEAD >"${artifacts}/revision.txt" 2>/dev/null ||
  printf 'No commit exists; working tree run\n' >"${artifacts}/revision.txt"
git status --short >"${artifacts}/working-tree.txt"
if [[ "${driver}" == true || "${operations}" == true ]]; then
  dotnet --info >"${artifacts}/dotnet-info.txt"
fi

# The matrix must use the checked-in pins and must not collide with developer ensembles.
unset ZOOKEEPER_IMAGE
export ZOOKEEPER_PORT_1=0 ZOOKEEPER_PORT_2=0 ZOOKEEPER_PORT_3=0
project=""
compose() {
  docker compose -f compose.yaml --env-file "dev/zookeeper-${version}.env" --project-name "${project}" "$@"
}
cleanup() {
  local status=$?
  trap - EXIT
  if [[ -n "${project}" ]]; then
    if ! compose logs --no-color >>"${artifacts}/${version}-server.log" 2>&1; then
      status=1
    fi
    if ! compose down --volumes >>"${artifacts}/${version}-cleanup.log" 2>&1; then
      echo "Cleanup failed for ${project}; see ${artifacts}" >&2
      status=1
    fi
  fi
  exit "${status}"
}
trap cleanup EXIT
trap 'exit 130' INT
trap 'exit 143' TERM

for version in 3.8.7 3.9.6; do
  project="zklink-matrix-${version//./-}-$(date +%s)-$$"
  echo "Checking ZooKeeper ${version}"
  compose config >"${artifacts}/${version}-compose.yaml"
  if ! compose up -d --wait --wait-timeout 90 >"${artifacts}/${version}-startup.log" 2>&1; then
    echo "ZooKeeper ${version} startup/readiness failed; see ${artifacts}" >&2
    exit 1
  fi
  for service in zoo1 zoo2 zoo3; do
    compose exec -T "${service}" zkServer.sh version >"${artifacts}/${version}-${service}-version.txt" 2>&1
    if ! grep -Fq "Apache ZooKeeper, version ${version} " "${artifacts}/${version}-${service}-version.txt"; then
      echo "Unexpected ZooKeeper version on ${service} for ${version}; see ${artifacts}" >&2
      exit 1
    fi
    compose exec -T "${service}" bash /opt/zklink/zookeeper-healthcheck.sh
    if [[ "${driver}" == true || "${operations}" == true ]]; then
      endpoint=$(compose port "${service}" 2181)
      sample_args=("${endpoint}" --plaintext)
      if [[ "${operations}" == true ]]; then
        sample_args+=(--nodes --transactions)
      fi
      ZKLINK_USER="zklink-connection-smoke" ZKLINK_PASSWORD="$(openssl rand -hex 16)" \
        dotnet run --project samples/ZkLink.Samples.Basic -c Release --no-build -- \
        "${sample_args[@]}" >"${artifacts}/${version}-${service}-driver.txt" 2>&1
    fi
  done
  if [[ "${operations}" == true ]]; then
    endpoints="$(compose port zoo1 2181),$(compose port zoo2 2181),$(compose port zoo3 2181)"
    if ! ZKLINK_TEST_CONNECTION_STRING="${endpoints}" ZKLINK_TEST_SERVER_VERSION="${version}" \
      dotnet test --project tests/ZkLink.IntegrationTests -c Release --no-build --no-restore \
      --results-directory "${artifacts}/${version}-results" >"${artifacts}/${version}-operations.txt" 2>&1; then
      cat "${artifacts}/${version}-operations.txt" >&2
      echo "ZkLink node/ACL tests failed on ${version}; see ${artifacts}" >&2
      exit 1
    fi
    cat "${artifacts}/${version}-operations.txt"
  fi
  compose ps --format json >"${artifacts}/${version}-status.json"
  compose logs --no-color >"${artifacts}/${version}-server.log" 2>&1
  compose down --volumes >"${artifacts}/${version}-cleanup.log" 2>&1
  project=""
  echo "ZooKeeper ${version}: PASS (three nodes, exact server version, authenticated create/read/delete, cleanup)"
  if [[ "${driver}" == true ]]; then
    echo "ZkLink ${version}: PASS (new session, digest auth, sync, acknowledged close on every node)"
  fi
  if [[ "${operations}" == true ]]; then
    echo "ZkLink ${version}: PASS (managed node/ACL/transaction integration tests, CRUD and transaction samples on every node, cleanup)"
  fi
done
