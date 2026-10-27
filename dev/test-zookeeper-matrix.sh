#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")/.."

driver=false
operations=false
ttl=false
if [[ $# -eq 1 && "$1" == "--driver" ]]; then
  driver=true
elif [[ $# -eq 1 && "$1" == "--operations" ]]; then
  operations=true
elif [[ $# -eq 1 && "$1" == "--ttl" ]]; then
  ttl=true
elif [[ $# -ne 0 ]]; then
  echo "Usage: $0 [--driver|--operations|--ttl]" >&2
  exit 2
fi
if [[ "${operations}" == true || "${ttl}" == true ]]; then
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
if [[ "${driver}" == true || "${operations}" == true || "${ttl}" == true ]]; then
  dotnet --info >"${artifacts}/dotnet-info.txt"
fi

# The matrix must use the checked-in pins and must not collide with developer ensembles.
unset ZOOKEEPER_IMAGE
export ZOOKEEPER_PORT_1=0 ZOOKEEPER_PORT_2=0 ZOOKEEPER_PORT_3=0
project=""
compose() {
  local files=(-f compose.yaml)
  if [[ "${ttl_setting}" == "enabled" ]]; then
    files+=(-f compose.ttl.yaml)
  fi
  docker compose "${files[@]}" --env-file "dev/zookeeper-${version}.env" --project-name "${project}" "$@"
}
cleanup() {
  local status=$?
  trap - EXIT
  if [[ -n "${project}" ]]; then
    if ! compose logs --no-color >>"${artifacts}/${fixture_key}-server.log" 2>&1; then
      status=1
    fi
    if ! compose down --volumes >>"${artifacts}/${fixture_key}-cleanup.log" 2>&1; then
      echo "Cleanup failed for ${project}; see ${artifacts}" >&2
      status=1
    fi
  fi
  exit "${status}"
}
trap cleanup EXIT
trap 'exit 130' INT
trap 'exit 143' TERM

ttl_settings=(disabled)
if [[ "${ttl}" == true ]]; then
  ttl_settings+=(enabled)
fi
for version in 3.8.7 3.9.6; do
  for ttl_setting in "${ttl_settings[@]}"; do
    fixture_key="${version}"
    if [[ "${ttl}" == true ]]; then
      fixture_key+="-ttl-${ttl_setting}"
    fi
    project="zklink-matrix-${fixture_key//./-}-$(date +%s)-$$"
    echo "Checking ZooKeeper ${fixture_key}"
    compose config >"${artifacts}/${fixture_key}-compose.yaml"
    if ! compose up -d --wait --wait-timeout 90 >"${artifacts}/${fixture_key}-startup.log" 2>&1; then
      echo "ZooKeeper ${version} startup/readiness failed; see ${artifacts}" >&2
      exit 1
    fi
    for service in zoo1 zoo2 zoo3; do
      compose exec -T "${service}" zkServer.sh version >"${artifacts}/${fixture_key}-${service}-version.txt" 2>&1
      if ! grep -Fq "Apache ZooKeeper, version ${version} " "${artifacts}/${fixture_key}-${service}-version.txt"; then
        echo "Unexpected ZooKeeper version on ${service} for ${version}; see ${artifacts}" >&2
        exit 1
      fi
      compose exec -T "${service}" bash /opt/zklink/zookeeper-healthcheck.sh
      if [[ "${driver}" == true || "${operations}" == true ]]; then
        endpoint=$(compose port "${service}" 2181)
        sample_args=("${endpoint}" --plaintext)
        if [[ "${operations}" == true ]]; then
          sample_args+=(--nodes --transactions --auxiliary --watches)
        fi
        ZKLINK_USER="zklink-connection-smoke" ZKLINK_PASSWORD="$(openssl rand -hex 16)" \
          dotnet run --project samples/ZkLink.Samples.Basic -c Release --no-build -- \
          "${sample_args[@]}" >"${artifacts}/${fixture_key}-${service}-driver.txt" 2>&1
      fi
    done
    if [[ "${operations}" == true || "${ttl}" == true ]]; then
      test_filter=(--filter-not-trait "Fixture=TTL" --filter-not-class "*Security*")
      if [[ "${ttl}" == true ]]; then
        if [[ "${ttl_setting}" == "enabled" ]]; then
          test_filter=(--filter-class "*TtlEnabledTests")
        else
          test_filter=(--filter-class "*TtlDisabledTests")
        fi
      fi
      endpoints="$(compose port zoo1 2181),$(compose port zoo2 2181),$(compose port zoo3 2181)"
      if ! ZKLINK_TEST_CONNECTION_STRING="${endpoints}" ZKLINK_TEST_SERVER_VERSION="${version}" ZKLINK_TEST_TTL_SETTING="${ttl_setting}" \
        dotnet test --project tests/ZkLink.IntegrationTests -c Release --no-build --no-restore \
        "${test_filter[@]}" --results-directory "${artifacts}/${fixture_key}-results" >"${artifacts}/${fixture_key}-operations.txt" 2>&1; then
        cat "${artifacts}/${fixture_key}-operations.txt" >&2
        echo "ZkLink operation tests failed on ${fixture_key}; see ${artifacts}" >&2
        exit 1
      fi
      cat "${artifacts}/${fixture_key}-operations.txt"
    fi
    compose ps --format json >"${artifacts}/${fixture_key}-status.json"
    compose logs --no-color >"${artifacts}/${fixture_key}-server.log" 2>&1
    compose down --volumes >"${artifacts}/${fixture_key}-cleanup.log" 2>&1
    project=""
    echo "ZooKeeper ${fixture_key}: PASS (three nodes, exact server version, authenticated create/read/delete, cleanup)"
    if [[ "${driver}" == true ]]; then
      echo "ZkLink ${version}: PASS (new session, digest auth, sync, acknowledged close on every node)"
    fi
    if [[ "${operations}" == true ]]; then
      echo "ZkLink ${version}: PASS (managed node/ACL/transaction/auxiliary/watch integration tests and samples on every node, cleanup)"
    fi
    if [[ "${ttl}" == true ]]; then
      echo "ZkLink ${fixture_key}: PASS (explicit TTL opt-in, ${ttl_setting} server behavior, cleanup)"
    fi
  done
done
