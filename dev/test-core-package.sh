#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")/.."
if [[ $# -gt 1 ]]; then
  echo "Usage: $0 [package-version]" >&2
  exit 2
fi

readonly startup_seconds=90
readonly process_guard_seconds=120
if ! preparation=$(python3 dev/prepare-core-package.py "$@"); then
  printf '%s\n' "${preparation}" >&2
  exit 1
fi
printf '%s\n' "${preparation}"
manifest=$(sed -n 's/^.*Manifest: //p' <<<"${preparation}")
IFS=$'\t' read -r run binary assembly_hash < <(python3 - "${manifest}" <<'PYTHON'
import sys
sys.path.insert(0, 'dev')
from core_package_artifacts import read_manifest
manifest, data = read_manifest(sys.argv[1])
print('\t'.join([str(manifest.parent), str(manifest.parent / data['consumer']), data['assemblySha256']]))
PYTHON
)
# read must fail before any fixture starts if the manifest check failed.
if [[ -z "${run}" || -z "${binary}" || -z "${assembly_hash}" ]]; then
  echo "Missing verified package consumer manifest." >&2
  exit 1
fi
readonly run binary manifest
export ZKLINK_PACKAGE_ASSEMBLY_SHA256="${assembly_hash}"
nonce=$(python3 -c 'import sys; sys.path.insert(0, "dev"); from core_package_artifacts import candidate_nonce; print(candidate_nonce())')
readonly nonce
docker compose version >"${run}/compose-version.txt"
docker version >"${run}/docker-version.txt"
docker info >/dev/null

unset ZOOKEEPER_IMAGE
export ZOOKEEPER_PORT_1=0 ZOOKEEPER_PORT_2=0 ZOOKEEPER_PORT_3=0
project=""
server_version=""
compose() {
  docker compose -f compose.yaml --env-file "dev/zookeeper-${server_version}.env" --project-name "${project}" "$@"
}
finish_fixture() {
  local status=0
  compose logs --no-color >>"${run}/${server_version}-server.log" 2>&1 || status=1
  compose ps --format json >>"${run}/${server_version}-status.json" 2>&1 || status=1
  for service in zoo1 zoo2 zoo3; do
    if compose exec -T "${service}" bash /opt/zklink/zookeeper-healthcheck.sh \
      >>"${run}/${server_version}-${service}-final-readiness.txt" 2>&1; then
      printf 'Authenticated readiness: PASS\n' >>"${run}/${server_version}-${service}-final-readiness.txt"
    else
      status=1
    fi
  done
  if compose down --volumes >>"${run}/${server_version}-cleanup.txt" 2>&1; then
    project=""
  else
    status=1
  fi
  return "${status}"
}
cleanup() {
  local status=$?
  trap - EXIT
  if [[ -n "${project}" ]]; then
    finish_fixture || status=1
  fi
  exit "${status}"
}
trap cleanup EXIT
trap 'exit 130' INT
trap 'exit 143' TERM

for server_version in 3.8.7 3.9.6; do
  project="zklink-package-${server_version//./-}-${nonce}"
  compose config >"${run}/${server_version}-compose.yaml"
  compose up -d --wait --wait-timeout "${startup_seconds}" >"${run}/${server_version}-startup.txt" 2>&1
  for service in zoo1 zoo2 zoo3; do
    compose exec -T "${service}" zkServer.sh version >"${run}/${server_version}-${service}-version.txt" 2>&1
    grep -Fq "Apache ZooKeeper, version ${server_version} " "${run}/${server_version}-${service}-version.txt"
    compose exec -T "${service}" bash /opt/zklink/zookeeper-healthcheck.sh >"${run}/${server_version}-${service}-readiness.txt" 2>&1
    endpoint=$(compose port "${service}" 2181)
    # Reuse the existing process-group guard; a managed deadline alone cannot bound a stuck host.
    ZKLINK_USER=zklink-package-consumer ZKLINK_PASSWORD="$(openssl rand -hex 16)" \
      python3 dev/run-live-benchmark-process.py "${process_guard_seconds}" dotnet "${binary}" \
      "${endpoint}" --plaintext --nodes --transactions --auxiliary --watches \
      >"${run}/${server_version}-${service}-consumer.txt" 2>&1
    grep -Fq 'Close: Acknowledged' "${run}/${server_version}-${service}-consumer.txt"
  done
  finish_fixture
  project=""
  echo "Installed core package on ZooKeeper ${server_version}: PASS (three nodes, examples, acknowledged close and cleanup)"
done
printf 'PASS\n' >"${run}/result.txt"
echo "Core package checks: PASS (local candidate only)"
