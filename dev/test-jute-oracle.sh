#!/usr/bin/env bash
set -euo pipefail

task_root="$(cd "$(dirname "$0")/.." && pwd)"
task_output="$task_root/artifacts/jute-oracle"
task_wrapper="$task_root/tests/ZkLink.JavaOracle/gradlew"
task_project="$task_root/tests/ZkLink.JavaOracle"
mkdir -p "$task_output"
task_java_output="$(mktemp -d "$task_output/java.XXXXXX")"
task_csharp_output="$(mktemp -d "$task_output/csharp.XXXXXX")"

bash "$task_wrapper" -p "$task_project" --project-cache-dir "$task_root/artifacts/java-oracle-cache" --no-daemon "-PoracleOutput=$task_java_output" verifyFixtures
cd "$task_root"
dotnet restore ZkLink.slnx --locked-mode
ZKLINK_ORACLE_OUTPUT="$task_csharp_output" ZKLINK_ALLOCATION_OUTPUT="$task_output/allocation-baseline.json" dotnet test --project tests/ZkLink.Protocol.Tests -c Release --no-restore
bash "$task_wrapper" -p "$task_project" --project-cache-dir "$task_root/artifacts/java-oracle-cache" --no-daemon "-PcsharpOutput=$task_csharp_output" verifyCSharpRequests
echo "Java fixture regeneration and C# request verification passed. Artifacts: $task_output"
