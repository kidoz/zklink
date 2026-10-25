#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")/.."

docker compose version >/dev/null
docker info >/dev/null
artifacts=$(mktemp -d "${TMPDIR:-/tmp}/zklink-zookeeper-matrix.XXXXXX")
echo "ZooKeeper matrix artifacts: ${artifacts}"
docker version >"${artifacts}/docker-version.txt"
docker compose version >"${artifacts}/compose-version.txt"
git rev-parse --verify HEAD >"${artifacts}/revision.txt" 2>/dev/null ||
  printf 'No commit exists; working tree run\n' >"${artifacts}/revision.txt"
git status --short >"${artifacts}/working-tree.txt"

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
  done
  compose ps --format json >"${artifacts}/${version}-status.json"
  compose logs --no-color >"${artifacts}/${version}-server.log" 2>&1
  compose down --volumes >"${artifacts}/${version}-cleanup.log" 2>&1
  project=""
  echo "ZooKeeper ${version}: PASS (three nodes, exact server version, authenticated create/read/delete, cleanup)"
done
