#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")/.."
versions=(3.8.7 3.9.6)
modes=(tls mtls)
concurrent=false
if [[ $# -gt 0 ]]; then
  if [[ "$1" != "--concurrent" || $# -gt 2 ]]; then
    echo "Usage: $0 [--concurrent [3.8.7|3.9.6]]" >&2
    exit 2
  fi
  concurrent=true
  modes=(mtls)
  if [[ $# -eq 2 ]]; then
    if [[ "$2" != 3.8.7 && "$2" != 3.9.6 ]]; then exit 2; fi
    versions=("$2")
  fi
fi
for command in docker dotnet openssl python3; do command -v "${command}" >/dev/null; done
docker compose version >/dev/null
docker info >/dev/null
dotnet restore tests/ZkLink.IntegrationTests --locked-mode
dotnet build tests/ZkLink.IntegrationTests -c Release --no-restore
artifact_root=${ZKLINK_MATRIX_ARTIFACT_ROOT:-${TMPDIR:-/tmp}}
mkdir -p "${artifact_root}"
artifacts=$(mktemp -d "${artifact_root%/}/zklink-security-matrix.XXXXXX")
# The PKI lives separately from retained evidence; never upload this directory.
private_root=$(mktemp -d "${TMPDIR:-/tmp}/zklink-security-private.XXXXXX")
project=""
compose() {
  docker compose -f compose.security.yaml --env-file "dev/zookeeper-${version}.env" --project-name "${project}" "$@"
}
sanitize_artifacts() {
  python3 - "${artifacts}" "${private_root}/server/store-password" <<'PY_SCAN'
from pathlib import Path
import sys
root=Path(sys.argv[1]); password=Path(sys.argv[2]).read_bytes().strip(); leaked=False
for path in root.rglob('*'):
    if path.is_symlink():
        raise SystemExit('Unexpected link in fixture evidence')
    if path.is_file():
        content=path.read_bytes()
        if path.suffix.lower() in {'.key', '.p12', '.pfx', '.pem'} or password in content or b'PRIVATE KEY-----' in content:
            path.unlink()
            leaked=True
if leaked:
    raise SystemExit('Removed sensitive fixture evidence; security run failed')
print('Retained evidence contains no private keys or store password.')
PY_SCAN
}
cleanup() {
  local status=$?
  trap - EXIT
  if [[ -n "${project}" ]]; then
    compose logs --no-color >>"${artifacts}/${fixture_key}-server.log" 2>&1 || status=1
    compose down --volumes >>"${artifacts}/${fixture_key}-cleanup.log" 2>&1 || status=1
  fi
  if [[ -f "${private_root}/server/store-password" ]]; then
    sanitize_artifacts || status=1
  fi
  # Delete only the mktemp directory owned by this invocation, never a user path.
  python3 - "${private_root}" <<'PY'
from pathlib import Path
import shutil, sys
path=Path(sys.argv[1])
if not path.is_absolute() or path.is_symlink() or not path.name.startswith('zklink-security-private.'):
    raise SystemExit('Refusing unexpected private fixture directory')
shutil.rmtree(path)
PY
  exit "${status}"
}
trap cleanup EXIT
trap 'exit 130' INT
trap 'exit 143' TERM
unset ZOOKEEPER_IMAGE
export ZKLINK_SECURITY_SERVER_DIRECTORY="${private_root}/server"
echo "ZooKeeper security artifacts: ${artifacts}"
docker version >"${artifacts}/docker-version.txt"
docker compose version >"${artifacts}/compose-version.txt"
dotnet --info >"${artifacts}/dotnet-info.txt"
openssl version >"${artifacts}/openssl-version.txt"
git rev-parse HEAD >"${artifacts}/revision.txt"
git status --short >"${artifacts}/working-tree.txt"
bash dev/generate-security-certificates.sh "${private_root}"
# Use the pinned test image's JDK; the host needs no Java/keytool installation.
keytool_image=$(sed -n 's/^ZOOKEEPER_IMAGE=//p' dev/zookeeper-3.9.6.env)
docker run --rm --user "$(id -u):$(id -g)" --entrypoint keytool \
  --mount "type=bind,source=${ZKLINK_SECURITY_SERVER_DIRECTORY},target=/pki" "${keytool_image}" \
  -importcert -noprompt -alias fixture-root -file /pki/root.pem -keystore /pki/trust.p12 \
  -storetype PKCS12 -storepass:file /pki/store-password >/dev/null 2>&1
chmod 644 "${ZKLINK_SECURITY_SERVER_DIRECTORY}/trust.p12"
# Public certificate metadata establishes expiry/trust without retaining key material.
for certificate in root server server-expired client other-client expired-client untrusted-client; do
  openssl x509 -in "${private_root}/${certificate}.pem" -noout -subject -issuer -dates -fingerprint -sha256 \
    >"${artifacts}/${certificate}-certificate.txt"
done
for version in "${versions[@]}"; do
  for mode in "${modes[@]}"; do
    fixture_key="${version}-${mode}"
    project="zklink-security-${fixture_key//./-}-$(date +%s)-$$"
    export ZKLINK_SERVER_CLIENT_AUTH=none
    if [[ "${mode}" == mtls ]]; then export ZKLINK_SERVER_CLIENT_AUTH=need; fi
    echo "Checking ZooKeeper ${fixture_key}"
    compose config >"${artifacts}/${fixture_key}-compose.yaml"
    if ! compose up -d --wait --wait-timeout 100 >"${artifacts}/${fixture_key}-startup.log" 2>&1; then
      echo "Security fixture startup/readiness failed; see ${artifacts}" >&2
      exit 1
    fi
    for service in zoo1 zoo2 zoo3 expired; do
      compose exec -T "${service}" zkServer.sh version >"${artifacts}/${fixture_key}-${service}-version.txt" 2>&1
      if ! grep -Fq "Apache ZooKeeper, version ${version} " "${artifacts}/${fixture_key}-${service}-version.txt"; then
        echo "Unexpected server version; see ${artifacts}" >&2
        exit 1
      fi
      compose exec -T "${service}" bash /opt/zklink/zookeeper-healthcheck.sh
    done
    endpoints="$(compose port zoo1 2281),$(compose port zoo2 2281),$(compose port zoo3 2281)"
    test_filter=(--filter-trait "Fixture=Security")
    if [[ "${concurrent}" == true ]]; then
      test_filter=(--filter-trait "Fixture=SecurityConcurrency" --output Detailed --show-stdout All --show-live-output on --timeout 2m)
    elif [[ "${mode}" == tls ]]; then
      test_filter+=(--filter-not-trait "Transport=MutualOnly")
    fi
    if ! ZKLINK_TEST_CONNECTION_STRING="${endpoints}" ZKLINK_TEST_SERVER_VERSION="${version}" \
      ZKLINK_TEST_SECURITY_MODE="${mode}" ZKLINK_TEST_CERTIFICATE_DIRECTORY="${private_root}" \
      ZKLINK_TEST_PLAINTEXT_ENDPOINT="$(compose port zoo1 2181)" \
      ZKLINK_TEST_EXPIRED_ENDPOINT="$(compose port expired 2281)" \
      dotnet test --project tests/ZkLink.IntegrationTests -c Release --no-build --no-restore \
      "${test_filter[@]}" --results-directory "${artifacts}/${fixture_key}-results" \
      >"${artifacts}/${fixture_key}-tests.txt" 2>&1; then
      echo "Secure tests failed; see ${artifacts}" >&2
      exit 1
    fi
    cat "${artifacts}/${fixture_key}-tests.txt"
    compose ps --format json >"${artifacts}/${fixture_key}-status.json"
    compose logs --no-color >"${artifacts}/${fixture_key}-server.log" 2>&1
    compose down --volumes >"${artifacts}/${fixture_key}-cleanup.log" 2>&1
    project=""
    if [[ "${concurrent}" == true ]]; then
      echo "ZooKeeper ${fixture_key}: PASS (concurrent rejection rounds, valid controls, cleanup)"
    else
      echo "ZooKeeper ${fixture_key}: PASS (secure readiness, .NET operations/reconnect/rejections, cleanup)"
    fi
  done
done
