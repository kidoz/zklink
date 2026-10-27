#!/usr/bin/env bash
set -euo pipefail

task_root="$(cd "$(dirname "$0")/.." && pwd)"
task_output="$task_root/artifacts/analyzers-package"
task_consumer="$task_root/tests/ZkLink.Analyzers.PackageSmoke/ZkLink.Analyzers.PackageSmoke.csproj"
task_version="0.0.0-local"
mkdir -p "$task_output"

dotnet pack "$task_root/src/ZkLink.Analyzers/ZkLink.Analyzers.csproj" -c Release -o "$task_output" -p:Version="$task_version"
dotnet restore "$task_consumer" --source "$task_output" --source "https://api.nuget.org/v3/index.json"
dotnet build "$task_consumer" -c Release --no-restore > "$task_output/clean-consumer.log" 2>&1

if grep -q "ZKL[0-9]" "$task_output/clean-consumer.log"; then
    cat "$task_output/clean-consumer.log"
    echo "The clean analyzer package consumer unexpectedly reported a ZkLink diagnostic." >&2
    exit 1
fi

if dotnet build "$task_consumer" -c Release --no-restore -p:DefineConstants=EXPECT_ANALYZER_DIAGNOSTIC > "$task_output/diagnostic-consumer.log" 2>&1; then
    cat "$task_output/diagnostic-consumer.log"
    echo "The analyzer package consumer unexpectedly compiled an unobserved call." >&2
    exit 1
fi

if ! grep -q "error ZKL0004" "$task_output/diagnostic-consumer.log"; then
    cat "$task_output/diagnostic-consumer.log"
    echo "The analyzer package consumer failed without the expected ZKL0004 diagnostic." >&2
    exit 1
fi

echo "Analyzer package consumer passed clean and ZKL0004 diagnostic checks. Artifacts: $task_output"
