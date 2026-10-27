#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")/.."
project=benchmarks/ZkLink.Benchmarks
dotnet restore "${project}" --locked-mode
dotnet build "${project}" -c Release --no-restore
artifact_root=${ZKLINK_BENCHMARK_ARTIFACT_ROOT:-${TMPDIR:-/tmp}}
mkdir -p "${artifact_root}"
artifacts=$(mktemp -d "${artifact_root%/}/zklink-benchmarks.XXXXXX")
artifacts=$(cd "${artifacts}" && pwd)
echo "Benchmark artifacts: ${artifacts}"
dotnet --info >"${artifacts}/dotnet-info.txt"
git rev-parse HEAD >"${artifacts}/revision.txt"
git status --short >"${artifacts}/working-tree.txt"
cp "${project}/allocation-budgets.json" "${artifacts}/allocation-budgets.json"
printf '%s\n' 'Server: none' 'Network: none; synthetic Jute records and synchronous MemoryStream framing' >"${artifacts}/workload-environment.txt"
dotnet run --project "${project}" -c Release --no-build --no-restore -- \
  --allocations "${artifacts}/allocations.json" >"${artifacts}/allocation-check.log" 2>&1 || {
    cat "${artifacts}/allocation-check.log"; exit 1;
  }
cat "${artifacts}/allocation-check.log"
if [[ $# -eq 0 ]]; then set -- --filter '*' --job Short; fi
dotnet run --project "${project}" -c Release --no-build --no-restore -- \
  --artifacts "${artifacts}" "$@" >"${artifacts}/benchmarks.log" 2>&1 || {
    cat "${artifacts}/benchmarks.log"; exit 1;
  }
cat "${artifacts}/benchmarks.log"
