#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")/.."
if [[ $# -gt 1 ]]; then
  echo "Usage: $0 [package-version]" >&2
  exit 2
fi

readonly startup_seconds=90
readonly process_guard_seconds=120
readonly artifact_root="${PWD}/artifacts/packages"
mkdir -p "${artifact_root}"
run=$(mktemp -d "${artifact_root}/run.XXXXXX")
readonly run
revision=$(git rev-parse --verify HEAD)
readonly revision
nonce=$(openssl rand -hex 8)
readonly nonce
version="0.0.0-local.r${revision:0:12}.n${nonce}"
if [[ $# -eq 1 ]]; then
  version=$(python3 dev/release_version.py --version "$1")
fi
readonly version
readonly feed="${run}/feed"
readonly consumer="${run}/consumer"
readonly inspector="${PWD}/tests/ZkLink.PackageValidation/bin/Release/net10.0/ZkLink.PackageValidation.dll"
mkdir -p "${feed}" "${consumer}/Samples"
echo "Core package artifacts: ${run}"
git status --short >"${run}/working-tree.txt"
printf '%s\n' "${revision}" >"${run}/revision.txt"
printf '%s\n' "${version}" >"${run}/version.txt"
dotnet --info >"${run}/dotnet-info.txt"
cp src/ZkLink/PublicAPI.*.txt "${run}/"
docker compose version >"${run}/compose-version.txt"
docker version >"${run}/docker-version.txt"
docker info >/dev/null

dotnet restore src/ZkLink --locked-mode >"${run}/restore.txt" 2>&1
dotnet pack src/ZkLink -c Release --no-restore -p:Version="${version}" \
  -p:ContinuousIntegrationBuild=true -o "${feed}" >"${run}/pack.txt" 2>&1
dotnet restore tests/ZkLink.PackageValidation --locked-mode >"${run}/inspector-restore.txt" 2>&1
dotnet build tests/ZkLink.PackageValidation -c Release --no-restore >"${run}/inspector-build.txt" 2>&1
dotnet "${inspector}" package "${feed}/ZkLink.${version}.nupkg" "${feed}/ZkLink.${version}.snupkg" \
  "${version}" "${revision}" "${run}/validation.json" >"${run}/validation.txt" 2>&1

# Build a standalone copy of the public examples. There is no source-project reference
# or inherited repository build configuration, and no public package resolution source.
cp tests/ZkLink.PackageConsumer/*.* "${consumer}/"
for source in samples/ZkLink.Samples.Basic/*.cs; do
  if [[ "${source##*/}" != Program.cs ]]; then
    cp "${source}" "${consumer}/Samples/"
  fi
done
printf '<Project />\n' >"${consumer}/Directory.Build.props"
python3 - "${feed}" "${consumer}" <<'PY'
import pathlib
import sys
import xml.etree.ElementTree as ET
feed, consumer = sys.argv[1:]
root = ET.Element("configuration")
sources = ET.SubElement(root, "packageSources")
ET.SubElement(sources, "clear")
ET.SubElement(sources, "add", key="candidate", value=feed)
audit = ET.SubElement(root, "auditSources")
ET.SubElement(audit, "clear")
ET.SubElement(audit, "add", key="nuget.org", value="https://api.nuget.org/v3/index.json")
ET.ElementTree(root).write(pathlib.Path(consumer) / "NuGet.config", encoding="utf-8", xml_declaration=True)
PY
export NUGET_PACKAGES="${run}/package-cache"
dotnet restore "${consumer}" -p:ZkLinkPackageVersion="${version}" --force-evaluate >"${run}/consumer-restore.txt" 2>&1
dotnet restore "${consumer}" -p:ZkLinkPackageVersion="${version}" --locked-mode >"${run}/consumer-locked-restore.txt" 2>&1
dotnet build "${consumer}" -c Release -p:ZkLinkPackageVersion="${version}" --no-restore >"${run}/consumer-build.txt" 2>&1
readonly binary="${consumer}/bin/Release/net10.0/ZkLink.PackageConsumer.dll"
dotnet "${inspector}" consumer "${consumer}/obj/project.assets.json" \
  "${consumer}/bin/Release/net10.0/ZkLink.dll" "${run}/validation.json" >"${run}/consumer-assets.txt" 2>&1
python3 dev/test-core-package-rejections.py "${inspector}" "${run}" >"${run}/rejections.txt" 2>&1
export ZKLINK_PACKAGE_ASSEMBLY_SHA256
ZKLINK_PACKAGE_ASSEMBLY_SHA256=$(python3 - "${run}/validation.json" <<'PY'
import json
import sys
with open(sys.argv[1], encoding="utf-8") as source:
    print(json.load(source)["assemblySha256"])
PY
)
if ZKLINK_PACKAGE_ASSEMBLY_SHA256=incorrect dotnet "${binary}" >"${run}/wrong-loaded-assembly.txt" 2>&1; then
  echo "Consumer accepted an incorrect loaded assembly hash." >&2
  exit 1
fi
grep -Fq 'The loaded driver does not match the candidate package.' "${run}/wrong-loaded-assembly.txt"

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
