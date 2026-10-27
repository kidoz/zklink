#!/usr/bin/env bash
set -euo pipefail
exec bash "$(dirname "${BASH_SOURCE[0]}")/test-zookeeper-node-faults.sh" peer "$@"
