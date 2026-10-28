#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")/.."
if [[ $# -gt 1 ]]; then
  echo "Usage: $0 [native.json]" >&2
  exit 2
fi
if [[ $# -eq 0 ]]; then
  if ! preparation=$(python3 dev/prepare-aot.py); then
    printf '%s\n' "${preparation}" >&2
    exit 1
  fi
  printf '%s\n' "${preparation}"
  manifest=$(sed -n 's/^.*Manifest: //p' <<<"${preparation}")
else
  manifest=$1
fi
IFS=$'\t' read -r run rid < <(python3 - "${manifest}" <<'PY'
import sys
sys.path.insert(0, 'dev')
from aot_artifacts import read_manifest
manifest, data = read_manifest(sys.argv[1])
print(manifest.parent.as_posix() + '\t' + data['rid'])
PY
)
readonly run rid manifest
readonly startup_seconds=100
docker compose version >"${run}/compose-version.txt"
docker version >"${run}/docker-version.txt"
docker info >/dev/null
private_root=$(mktemp -d "${TMPDIR:-/tmp}/zklink-aot-private.XXXXXX")
readonly private_root
project=""
version=""
mode=""
fixture_key=""
completed=false
compose() {
  docker compose -f compose.security.yaml --env-file "dev/zookeeper-${version}.env" --project-name "${project}" "$@"
}
finish_fixture() {
  local status=0
  compose logs --no-color >>"${run}/${fixture_key}-server.log" 2>&1 || status=1
  compose ps --format json >>"${run}/${fixture_key}-status.json" 2>&1 || status=1
  for service in zoo1 zoo2 zoo3 expired; do
    if compose exec -T "${service}" bash /opt/zklink/zookeeper-healthcheck.sh >>"${run}/${fixture_key}-${service}-final-readiness.txt" 2>&1; then
      printf 'Authenticated readiness: PASS\n' >>"${run}/${fixture_key}-${service}-final-readiness.txt"
    else
      status=1
    fi
  done
  if compose down --volumes >>"${run}/${fixture_key}-cleanup.txt" 2>&1; then
    project=""
  else
    status=1
  fi
  return "${status}"
}
cleanup() {
  local status=$?
  trap - EXIT
  if [[ "${completed}" != true && "${status}" -eq 0 ]]; then status=1; fi
  if [[ -n "${project}" ]]; then
    finish_fixture || status=1
  fi
  if ! python3 - "${private_root}" "${run}" <<'PY'
import pathlib, shutil, sys
private, run = map(pathlib.Path, sys.argv[1:])
if not private.is_absolute() or private.is_symlink() or not private.name.startswith('zklink-aot-private.'):
    raise SystemExit('Refusing unexpected private fixture path.')
password_file = private / 'server/store-password'
leaked = False
try:
    if password_file.exists():
        password = password_file.read_bytes().strip()
        for path in run.rglob('*'):
            if path.is_file() and path.suffix in {'.txt', '.json', '.log', '.yaml'}:
                content = path.read_bytes()
                if password in content or b'PRIVATE KEY-----' in content:
                    path.unlink()
                    leaked = True
finally:
    shutil.rmtree(private)
if leaked:
    raise SystemExit('Removed sensitive evidence; native fixture verification failed.')
PY
  then
    status=1
  fi
  exit "${status}"
}
trap cleanup EXIT
trap 'exit 130' INT
trap 'exit 143' TERM
unset ZOOKEEPER_IMAGE
export ZKLINK_SECURITY_SERVER_DIRECTORY="${private_root}/server"
bash dev/generate-security-certificates.sh "${private_root}"
keytool_image=$(sed -n 's/^ZOOKEEPER_IMAGE=//p' dev/zookeeper-3.9.6.env)
docker run --rm --user "$(id -u):$(id -g)" --entrypoint keytool \
  --mount "type=bind,source=${ZKLINK_SECURITY_SERVER_DIRECTORY},target=/pki" "${keytool_image}" \
  -importcert -noprompt -alias fixture-root -file /pki/root.pem -keystore /pki/trust.p12 \
  -storetype PKCS12 -storepass:file /pki/store-password >"${run}/keytool.txt" 2>&1
chmod 644 "${ZKLINK_SECURITY_SERVER_DIRECTORY}/trust.p12"
openssl version >"${run}/openssl-version.txt"
for certificate in root server server-expired client other-client; do
  openssl x509 -in "${private_root}/${certificate}.pem" -noout -subject -issuer -dates -fingerprint -sha256 >"${run}/${certificate}-certificate.txt"
done
nonce=$(openssl rand -hex 8)
for version in 3.8.7 3.9.6; do
  for mode in tls mtls; do
    fixture_key="${version}-${mode}"
    project="zklink-aot-${fixture_key//./-}-${nonce}"
    export ZKLINK_SERVER_CLIENT_AUTH=none
    if [[ "${mode}" == mtls ]]; then export ZKLINK_SERVER_CLIENT_AUTH=need; fi
    compose config >"${run}/${fixture_key}-compose.yaml"
    compose up -d --wait --wait-timeout "${startup_seconds}" >"${run}/${fixture_key}-startup.txt" 2>&1
    for service in zoo1 zoo2 zoo3 expired; do
      compose exec -T "${service}" zkServer.sh version >"${run}/${fixture_key}-${service}-version.txt" 2>&1
      grep -Fq "Apache ZooKeeper, version ${version} " "${run}/${fixture_key}-${service}-version.txt"
      compose exec -T "${service}" bash /opt/zklink/zookeeper-healthcheck.sh >"${run}/${fixture_key}-${service}-readiness.txt" 2>&1
    done
    for service in zoo1 zoo2 zoo3; do
      options=("${manifest}")
      if [[ "${service}" == zoo1 ]]; then options+=(--security-negatives); fi
      ZKLINK_AOT_ENDPOINT="$(compose port "${service}" 2281)" \
        ZKLINK_AOT_SECURITY_MODE="${mode}" ZKLINK_AOT_SERVER_VERSION="${version}" \
        ZKLINK_AOT_CERTIFICATE_DIRECTORY="${private_root}" \
        ZKLINK_AOT_PLAINTEXT_ENDPOINT="$(compose port zoo1 2181)" \
        ZKLINK_AOT_EXPIRED_ENDPOINT="$(compose port expired 2281)" \
        ZKLINK_USER=zklink-native-smoke ZKLINK_PASSWORD="$(openssl rand -hex 16)" \
        python3 dev/run-native-smoke.py "${options[@]}" >"${run}/${fixture_key}-${service}-native.txt" 2>&1
      grep -Fxq 'Native smoke: PASS' "${run}/${fixture_key}-${service}-native.txt"
    done
    finish_fixture
    echo "Native ${rid} on ZooKeeper ${fixture_key}: PASS (three nodes, recovery, cancellation, acknowledged close and cleanup)"
  done
done
printf 'PASS (12 native live cases, TLS/mTLS, both server versions)\n' >"${run}/live-result.txt"
completed=true
