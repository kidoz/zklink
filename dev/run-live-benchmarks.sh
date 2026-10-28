#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")/.."
kind=requests
if [[ "${1-}" == --watches ]]; then kind=watches; shift;
elif [[ "${1-}" == --recovery ]]; then kind=recovery; shift; fi
if [[ $# -gt 1 ]]; then echo "Usage: $0 [--watches|--recovery] [seconds-or-watches]" >&2; exit 2; fi
seconds=5
watches=10000
if [[ "${kind}" == recovery ]]; then
  watches="${1-10000}"
  if [[ "${watches}" != 100 && "${watches}" != 10000 ]]; then
    echo "Recovery workload must use 100 or 10000 watches." >&2; exit 2
  fi
else
  seconds="${1-5}"
  if [[ ! "${seconds}" =~ ^([1-9]|[12][0-9]|30)$ ]]; then
    echo "Live benchmark duration must be 1..30 seconds per case." >&2; exit 2
  fi
fi
readonly kind seconds watches
for command in docker dotnet python3; do command -v "${command}" >/dev/null; done
docker info >/dev/null
docker compose version >/dev/null
dotnet restore benchmarks/ZkLink.Benchmarks --locked-mode
dotnet build benchmarks/ZkLink.Benchmarks -c Release --no-restore
artifact_root=${ZKLINK_BENCHMARK_ARTIFACT_ROOT:-${TMPDIR:-/tmp}}
mkdir -p "${artifact_root}"
artifacts=$(mktemp -d "${artifact_root%/}/zklink-live-benchmarks.XXXXXX")
artifacts=$(cd "${artifacts}" && pwd)
echo "Live benchmark artifacts: ${artifacts}"
dotnet --info >"${artifacts}/dotnet-info.txt"
docker version >"${artifacts}/docker-version.txt"
docker compose version >"${artifacts}/compose-version.txt"
docker info --format '{{.NCPU}} {{.MemTotal}} {{.KernelVersion}} {{.OperatingSystem}} {{.Architecture}}' >"${artifacts}/docker-host.txt"
git rev-parse HEAD >"${artifacts}/revision.txt"
git status --short >"${artifacts}/working-tree.txt"
python3 - "${artifacts}/host-machine.json" <<'MACHINE'
import json
import os
import platform
import subprocess
import sys
from pathlib import Path
HOST_QUERY_SECONDS = 5
info = {"OS": platform.platform(), "Architecture": platform.machine(),
        "LogicalProcessors": os.cpu_count(), "Processor": platform.processor(),
        "PhysicalMemoryBytes": os.sysconf("SC_PAGE_SIZE") * os.sysconf("SC_PHYS_PAGES")}
if platform.system() == "Darwin":
    info["Processor"] = subprocess.check_output(["sysctl", "-n", "machdep.cpu.brand_string"], text=True, timeout=HOST_QUERY_SECONDS).strip()
elif Path("/proc/cpuinfo").exists():
    for line in Path("/proc/cpuinfo").read_text().splitlines():
        if line.startswith("model name"):
            info["Processor"] = line.split(":", 1)[1].strip()
            break
Path(sys.argv[1]).write_text(json.dumps(info, indent=2) + "\n")
MACHINE
readonly startup_guard_seconds=90 process_overhead_seconds=90 recovery_process_guard_seconds=300
if [[ "${kind}" == recovery ]]; then
  printf 'Kind: recovery\nWatches: %s\nWarmupSeconds: 0\nProcessGuardSeconds: %s\nNetwork: inspecting loopback TCP proxy through Docker bridge; plaintext digest\n' "${watches}" "${recovery_process_guard_seconds}" >"${artifacts}/settings.txt"
else
  printf 'Kind: %s\nRequestedSeconds: %s\nWarmupSeconds: 1\nNetwork: loopback host TCP through Docker bridge; plaintext digest\n' "${kind}" "${seconds}" >"${artifacts}/settings.txt"
fi
unset ZOOKEEPER_IMAGE
export ZOOKEEPER_PORT_1=0 ZOOKEEPER_PORT_2=0 ZOOKEEPER_PORT_3=0
project=""
compose() {
  docker compose -f compose.yaml -f compose.benchmark.yaml --env-file "dev/zookeeper-${version}.env" --project-name "${project}" "$@"
}
finish_fixture() {
  local result=0
  for service in zoo1 zoo2 zoo3; do
    if compose exec -T "${service}" bash /opt/zklink/zookeeper-healthcheck.sh >>"${artifacts}/${version}-readiness-after.txt" 2>&1; then
      printf 'cleanup-authenticated-ready: %s\n' "${service}" >>"${artifacts}/${version}-readiness-after.txt"
    else
      result=1
    fi
  done
  compose logs --no-color >>"${artifacts}/${version}-server.log" 2>&1 || result=1
  if compose down --volumes >>"${artifacts}/${version}-cleanup.log" 2>&1; then project=""; else result=1; fi
  return "${result}"
}
cleanup() {
  local result=$?
  trap - EXIT
  if [[ -n "${project}" ]]; then finish_fixture || result=1; fi
  exit "${result}"
}
trap cleanup EXIT
trap 'exit 130' INT
trap 'exit 143' TERM
for version in 3.8.7 3.9.6; do
  export ZKLINK_LIVE_BENCHMARK_RUN_ID="$(python3 -c 'import uuid; print(uuid.uuid4().hex)')"
  export ZKLINK_LIVE_BENCHMARK_VERSION="${version}"
  project="zklink-benchmark-${version//./-}-${ZKLINK_LIVE_BENCHMARK_RUN_ID}"
  compose config >"${artifacts}/${version}-compose.yaml"
  compose up -d --wait --wait-timeout "${startup_guard_seconds}" >"${artifacts}/${version}-startup.log" 2>&1
  leader=""
  leaders=0
  followers=0
  for service in zoo1 zoo2 zoo3; do
    compose exec -T "${service}" zkServer.sh version >"${artifacts}/${version}-${service}-version.txt" 2>&1
    grep -Fq "Apache ZooKeeper, version ${version} " "${artifacts}/${version}-${service}-version.txt"
    compose exec -T "${service}" bash /opt/zklink/zookeeper-healthcheck.sh
    compose exec -T "${service}" zkServer.sh status >"${artifacts}/${version}-${service}-role.txt" 2>&1
    if grep -Fxq 'Mode: leader' "${artifacts}/${version}-${service}-role.txt"; then
      leader="${service}"; leaders=$((leaders + 1))
    elif grep -Fxq 'Mode: follower' "${artifacts}/${version}-${service}-role.txt"; then
      followers=$((followers + 1))
    fi
    container=$(compose ps -q "${service}")
    docker inspect --format '{{.Id}} {{.Image}} {{.HostConfig.NanoCpus}} {{.HostConfig.Memory}} {{.HostConfig.CpuQuota}} {{.HostConfig.CpusetCpus}} {{json .NetworkSettings.Networks}}' "${container}" >"${artifacts}/${version}-${service}-container.txt"
  done
  if [[ "${leaders}" -ne 1 || "${followers}" -ne 2 ]]; then echo 'No healthy voting ensemble.' >&2; exit 1; fi
  export ZKLINK_LIVE_BENCHMARK_ENDPOINT="$(compose port "${leader}" 2181)"
  printf 'Target: observed leader %s\nEndpoint: %s\n' "${leader}" "${ZKLINK_LIVE_BENCHMARK_ENDPOINT}" >"${artifacts}/${version}-target.txt"
  if [[ "${kind}" == recovery ]]; then
    key="${version}-recovery-${watches}"
    if ! python3 dev/run-live-benchmark-process.py "${recovery_process_guard_seconds}" \
      dotnet benchmarks/ZkLink.Benchmarks/bin/Release/net10.0/ZkLink.Benchmarks.dll \
      --watch-recovery "${watches}" "${artifacts}/${key}.json" >"${artifacts}/${key}.log" 2>&1; then
      cat "${artifacts}/${key}.log"; exit 1
    fi
    cat "${artifacts}/${key}.log"
  elif [[ "${kind}" == watches ]]; then
    for consumers in 1 32 256; do
      key="${version}-watches-${consumers}"
      if ! python3 dev/run-live-benchmark-process.py "$((seconds + process_overhead_seconds))" \
        dotnet benchmarks/ZkLink.Benchmarks/bin/Release/net10.0/ZkLink.Benchmarks.dll \
        --watch-fanout "${seconds}" "${consumers}" "${artifacts}/${key}.json" >"${artifacts}/${key}.log" 2>&1; then
        cat "${artifacts}/${key}.log"; exit 1
      fi
      cat "${artifacts}/${key}.log"
    done
  else
  for operation in get set; do
    for payload in 32 65536; do
      for concurrency in 1 32 256; do
        key="${version}-${operation}-${payload}-${concurrency}"
        if ! python3 dev/run-live-benchmark-process.py "$((seconds + process_overhead_seconds))" \
          dotnet benchmarks/ZkLink.Benchmarks/bin/Release/net10.0/ZkLink.Benchmarks.dll \
          --live "${seconds}" "${operation}" "${payload}" "${concurrency}" "${artifacts}/${key}.json" >"${artifacts}/${key}.log" 2>&1; then
          cat "${artifacts}/${key}.log"; exit 1
        fi
        cat "${artifacts}/${key}.log"
      done
    done
  done
  fi
  for service in zoo1 zoo2 zoo3; do
    compose exec -T "${service}" zkServer.sh status >"${artifacts}/${version}-${service}-role-after.txt" 2>&1
    expected=follower
    if [[ "${service}" == "${leader}" ]]; then expected=leader; fi
    grep -Fxq "Mode: ${expected}" "${artifacts}/${version}-${service}-role-after.txt"
  done
  compose ps --format json >"${artifacts}/${version}-status.json"
  finish_fixture
  project=""
  if [[ "${kind}" == recovery ]]; then
    echo "ZooKeeper ${version}: PASS (distinct-path recovery, native restore/gap counts, all-path validation, close and cleanup)"
  elif [[ "${kind}" == watches ]]; then
    echo "ZooKeeper ${version}: PASS (3 shared watch fan-out cases, exact delivery, request progress, counters, close and fixture cleanup)"
  else
    echo "ZooKeeper ${version}: PASS (12 live request benchmark cases, validation, acknowledged close and fixture cleanup)"
  fi
done
