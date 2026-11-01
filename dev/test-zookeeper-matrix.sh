#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")/.."

driver=false
operations=false
ttl=false
authentication=false
if [[ $# -eq 1 && "$1" == "--driver" ]]; then
  driver=true
elif [[ $# -eq 1 && "$1" == "--operations" ]]; then
  operations=true
elif [[ $# -eq 1 && "$1" == "--ttl" ]]; then
  ttl=true
elif [[ $# -eq 1 && "$1" == "--authentication" ]]; then
  authentication=true
elif [[ $# -ne 0 ]]; then
  echo "Usage: $0 [--driver|--operations|--ttl|--authentication]" >&2
  exit 2
fi
if [[ "${operations}" == true || "${ttl}" == true || "${authentication}" == true ]]; then
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
if [[ "${driver}" == true || "${operations}" == true || "${ttl}" == true || "${authentication}" == true ]]; then
  dotnet --info >"${artifacts}/dotnet-info.txt"
fi

# The matrix must use the checked-in pins and must not collide with developer ensembles.
unset ZOOKEEPER_IMAGE
export ZOOKEEPER_PORT_1=0 ZOOKEEPER_PORT_2=0 ZOOKEEPER_PORT_3=0
project=""
private_directory=""
compose() {
  local files=(-f compose.yaml)
  if [[ "${fixture_setting}" == "enabled" ]]; then
    files+=(-f compose.ttl.yaml)
  fi
  if [[ "${authentication}" == true ]]; then files+=(-f compose.authentication.yaml); fi
  docker compose "${files[@]}" --env-file "dev/zookeeper-${version}.env" --project-name "${project}" "$@"
}
verify_authentication_roles() {
  local checkpoint="$1"
  local leaders=0 followers=0
  for service in zoo1 zoo2 zoo3; do
    compose exec -T "${service}" zkServer.sh status >"${artifacts}/${fixture_key}-${service}-role-${checkpoint}.txt" 2>&1
    if grep -Fxq 'Mode: leader' "${artifacts}/${fixture_key}-${service}-role-${checkpoint}.txt"; then
      leaders=$((leaders + 1))
    elif grep -Fxq 'Mode: follower' "${artifacts}/${fixture_key}-${service}-role-${checkpoint}.txt"; then
      followers=$((followers + 1))
    else
      echo "Authentication fixture has an unexpected serving role" >&2
      return 1
    fi
  done
  [[ "${leaders}" -eq 1 && "${followers}" -eq 2 ]]
}
cleanup() {
  local status=$?
  trap - EXIT
  if [[ -n "${project}" ]]; then
    if [[ "${authentication}" == true ]]; then
      for service in zoo1 zoo2 zoo3; do
        if compose exec -T "${service}" bash /opt/zklink/zookeeper-healthcheck.sh >>"${artifacts}/${fixture_key}-rollback.log" 2>&1; then
          printf 'authentication-cleanup-ready: %s\n' "${service}" >>"${artifacts}/${fixture_key}-rollback.log"
        else
          status=1
        fi
      done
    fi
    if ! compose logs --no-color >>"${artifacts}/${fixture_key}-server.log" 2>&1; then
      status=1
    fi
    if ! compose down --volumes >>"${artifacts}/${fixture_key}-cleanup.log" 2>&1; then
      echo "Cleanup failed for ${project}; see ${artifacts}" >&2
      status=1
    fi
  fi
  if [[ -n "${private_directory}" ]]; then
    python3 - "${private_directory}" "${artifacts}" <<'PRIVATE_CLEANUP' || status=1
import json
import pathlib
import shutil
import sys
private, artifacts = map(pathlib.Path, sys.argv[1:])
passed = True
redacted = []
try:
    secrets = [json.loads((private / "credentials.json").read_text())["password"].encode()]
    secrets.extend(path.read_bytes() for path in private.glob("*.secret"))
    for path in artifacts.rglob("*"):
        if not path.is_file():
            continue
        original = path.read_bytes()
        safe = original
        for secret in secrets:
            if secret:
                safe = safe.replace(secret, b"[REDACTED]")
        if safe != original:
            passed = False
            path.write_bytes(safe)
            redacted.append(str(path.relative_to(artifacts)))
finally:
    shutil.rmtree(private)
(artifacts / "private-cleanup.json").write_text(json.dumps({
    "secret_scan_passed": passed, "redacted_files": redacted,
    "private_directory_removed": not private.exists()}, indent=2) + "\n")
if not passed:
    raise SystemExit("Authentication evidence contained private material and was redacted.")
PRIVATE_CLEANUP
  fi
  exit "${status}"
}
trap cleanup EXIT
trap 'exit 130' INT
trap 'exit 143' TERM

if [[ "${authentication}" == true ]]; then
  private_directory=$(mktemp -d "${TMPDIR:-/tmp}/zklink-authentication-private.XXXXXX")
  export ZKLINK_AUTH_PRIVATE_DIRECTORY="${private_directory}"
  python3 - "${private_directory}" <<'JAAS'
import json
import os
import pathlib
import secrets
import sys
private = pathlib.Path(sys.argv[1])
os.chmod(private, 0o700)
password = secrets.token_hex(24)
(private / "credentials.json").write_text(json.dumps({"password": password}))
os.chmod(private / "credentials.json", 0o600)
(private / "jaas.conf").write_text(
    'Server { org.apache.zookeeper.server.auth.DigestLoginModule required user_probe="' + password + '"; };\n'
    'Client { org.apache.zookeeper.server.auth.DigestLoginModule required username="probe" password="' + password + '"; };\n')
# Bind the file directly: host access remains protected by its private parent;
# the owned container's ZooKeeper user must be able to read the mounted file.
os.chmod(private / "jaas.conf", 0o644)
JAAS
fi
fixture_settings=(disabled)
if [[ "${ttl}" == true ]]; then
  fixture_settings+=(enabled)
fi
if [[ "${authentication}" == true ]]; then fixture_settings=(digest sasl); fi
for version in 3.8.7 3.9.6; do
  for fixture_setting in "${fixture_settings[@]}"; do
    fixture_key="${version}"
    if [[ "${ttl}" == true ]]; then
      fixture_key+="-ttl-${fixture_setting}"
    fi
    if [[ "${authentication}" == true ]]; then
      fixture_key+="-auth-${fixture_setting}"
      export ZKLINK_TEST_AUTH_SCHEME="${fixture_setting}"
      # Preserve each patch's default for digest; only the legacy SASL reference
      # fixture opts out of FIPS to allow DIGEST-MD5 in the bundled Java client/server.
      export ZKLINK_AUTH_FIPS_MODE=true
      if [[ "${version}" == 3.8.7 || "${fixture_setting}" == sasl ]]; then export ZKLINK_AUTH_FIPS_MODE=false; fi
      export ZKLINK_HEALTH_SASL=false
      if [[ "${fixture_setting}" == sasl ]]; then export ZKLINK_HEALTH_SASL=true; fi
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
    if [[ "${authentication}" == true ]]; then verify_authentication_roles before; fi
    if [[ "${operations}" == true || "${ttl}" == true || "${authentication}" == true ]]; then
      test_filter=(--filter-not-trait "Fixture=TTL" --filter-not-trait "Fixture=Observer" --filter-not-class "*Security*")
      if [[ "${authentication}" == true ]]; then
        test_filter=(--filter-trait "AuthenticationPolicy=${fixture_setting}")
      elif [[ "${ttl}" == true ]]; then
        if [[ "${fixture_setting}" == "enabled" ]]; then
          test_filter=(--filter-class "*TtlEnabledTests")
        else
          test_filter=(--filter-class "*TtlDisabledTests")
        fi
      fi
      test_execution=()
      if [[ "${authentication}" == true ]]; then
        test_execution=(--output Detailed --show-stdout All --show-live-output on --timeout 3m)
      fi
      endpoints="$(compose port zoo1 2181),$(compose port zoo2 2181),$(compose port zoo3 2181)"
      if ! ZKLINK_TEST_CONNECTION_STRING="${endpoints}" ZKLINK_TEST_SERVER_VERSION="${version}" ZKLINK_TEST_TTL_SETTING="${fixture_setting}" \
        dotnet test --project tests/ZkLink.IntegrationTests -c Release --no-build --no-restore \
        "${test_filter[@]}" "${test_execution[@]}" --results-directory "${artifacts}/${fixture_key}-results" >"${artifacts}/${fixture_key}-operations.txt" 2>&1; then
        cat "${artifacts}/${fixture_key}-operations.txt" >&2
        echo "ZkLink operation tests failed on ${fixture_key}; see ${artifacts}" >&2
        exit 1
      fi
      cat "${artifacts}/${fixture_key}-operations.txt"
    fi
    if [[ "${authentication}" == true ]]; then
      for service in zoo1 zoo2 zoo3; do
        compose exec -T "${service}" bash /opt/zklink/zookeeper-healthcheck.sh >>"${artifacts}/${fixture_key}-final-readiness.txt" 2>&1
        printf 'authentication-final-ready: %s\n' "${service}" >>"${artifacts}/${fixture_key}-final-readiness.txt"
      done
      verify_authentication_roles after
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
    if [[ "${authentication}" == true ]]; then
      echo "ZkLink ${fixture_key}: PASS (required ${fixture_setting} policy, typed outcomes, terminal or retained session, authenticated cleanup)"
    fi
    if [[ "${ttl}" == true ]]; then
      echo "ZkLink ${fixture_key}: PASS (explicit TTL opt-in, ${fixture_setting} server behavior, cleanup)"
    fi
  done
done
