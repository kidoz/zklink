#!/usr/bin/env bash
set -euo pipefail
# Called only with a fresh private temporary directory owned by the security runner.
if [[ $# -ne 1 || ! -d "$1" ]]; then
  echo "Usage: $0 existing-private-directory" >&2
  exit 2
fi
umask 077
pki=$(cd "$1" && pwd)
mkdir "${pki}/server"
openssl rand -hex 32 >"${pki}/server/store-password"
openssl req -x509 -newkey rsa:2048 -nodes -sha256 -days 2 \
  -subj /CN=zklink-fixture-root -keyout "${pki}/ca.key" -out "${pki}/root.pem" \
  -addext basicConstraints=critical,CA:TRUE -addext keyUsage=critical,keyCertSign,cRLSign >/dev/null 2>&1
openssl req -x509 -newkey rsa:2048 -nodes -sha256 -days 2 \
  -subj /CN=zklink-untrusted-root -keyout "${pki}/untrusted.key" -out "${pki}/untrusted-root.pem" \
  -addext basicConstraints=critical,CA:TRUE -addext keyUsage=critical,keyCertSign,cRLSign >/dev/null 2>&1
: >"${pki}/index"
printf '01\n' >"${pki}/serial"
cat >"${pki}/ca.cnf" <<CONFIG
[ca]
default_ca=fixture
[fixture]
database=${pki}/index
serial=${pki}/serial
new_certs_dir=${pki}
certificate=${pki}/root.pem
private_key=${pki}/ca.key
default_md=sha256
default_days=2
policy=subject
unique_subject=no
[subject]
commonName=supplied
[server]
basicConstraints=critical,CA:FALSE
keyUsage=critical,digitalSignature,keyEncipherment
extendedKeyUsage=serverAuth
subjectAltName=DNS:localhost,DNS:zklink.test,DNS:zoo1,DNS:zoo2,DNS:zoo3,IP:127.0.0.1
[client]
basicConstraints=critical,CA:FALSE
keyUsage=critical,digitalSignature,keyEncipherment
extendedKeyUsage=clientAuth
subjectAltName=DNS:localhost,IP:127.0.0.1
CONFIG
issue() {
  local name=$1 extension=$2 expired=$3
  openssl req -new -newkey rsa:2048 -nodes -subj "/CN=${name}" \
    -keyout "${pki}/${name}.key" -out "${pki}/${name}.csr" >/dev/null 2>&1
  local validity=(-days 2)
  if [[ "${expired}" == true ]]; then
    validity=(-startdate 20200101000000Z -enddate 20200102000000Z)
  fi
  openssl ca -batch -notext -config "${pki}/ca.cnf" -extensions "${extension}" \
    "${validity[@]}" -in "${pki}/${name}.csr" -out "${pki}/${name}.pem" >/dev/null 2>&1
  openssl pkcs12 -export -name "${name}" -inkey "${pki}/${name}.key" \
    -in "${pki}/${name}.pem" -certfile "${pki}/root.pem" \
    -out "${pki}/${name}.p12" -passout "file:${pki}/server/store-password"
}
issue server server false
issue server-expired server true
issue client client false
issue other-client client false
issue expired-client client true
openssl req -new -newkey rsa:2048 -nodes -subj /CN=untrusted-client \
  -keyout "${pki}/untrusted-client.key" -out "${pki}/untrusted-client.csr" >/dev/null 2>&1
openssl x509 -req -in "${pki}/untrusted-client.csr" -CA "${pki}/untrusted-root.pem" \
  -CAkey "${pki}/untrusted.key" -CAcreateserial -days 2 -sha256 \
  -extfile "${pki}/ca.cnf" -extensions client -out "${pki}/untrusted-client.pem" >/dev/null 2>&1
openssl pkcs12 -export -name untrusted-client -inkey "${pki}/untrusted-client.key" \
  -in "${pki}/untrusted-client.pem" -certfile "${pki}/untrusted-root.pem" \
  -out "${pki}/untrusted-client.p12" -passout "file:${pki}/server/store-password"
cp "${pki}/server.p12" "${pki}/server-expired.p12" "${pki}/client.p12" "${pki}/root.pem" "${pki}/server/"
# The parent stays private on the host. Only this nested bind mount is readable by
# the unprivileged ZooKeeper container account; CA keys never enter the mount.
chmod 755 "${pki}/server"
chmod 644 "${pki}/server/"*
