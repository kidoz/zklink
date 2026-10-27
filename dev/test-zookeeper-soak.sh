#!/usr/bin/env bash
set -euo pipefail
if [[ $# -gt 2 ]]; then echo "Usage: $0 [duration-seconds] [seed]" >&2; exit 2; fi
export ZKLINK_SOAK_DURATION_SECONDS="${1-120}"
export ZKLINK_SOAK_SEED="${2-1729}"
exec bash "$(dirname "${BASH_SOURCE[0]}")/test-zookeeper-node-faults.sh" soak
