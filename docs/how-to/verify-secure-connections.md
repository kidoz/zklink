# Verify secure connections

Run the dedicated client TLS and mutual TLS checks with:

```sh
just zk-security-matrix
```

Install the pinned .NET SDK, Docker with Compose, OpenSSL and Python 3. The runner
uses the pinned ZooKeeper image's keytool; host Java and Gradle are unnecessary
for this fixture. The separate Java protocol oracle continues to use Gradle.

The command creates isolated three-node ensembles for ZooKeeper 3.8.7 and 3.9.6,
once with server certificate authentication and once with required client
certificates. All published ports bind to loopback with dynamic host ports.
Each secure node must pass an authenticated create/read/delete probe through
its TLS client port before the .NET checks run.

Security cases share one test collection so certificate rejection and positive
controls run independently. Negative probes have a one-second connection budget
with the same rejection assertions and outer timeout guards; sustained concurrent
TLS failure traffic is outside this functional check.

Run the separate bounded concurrency check with:

```sh
just zk-security-concurrency
```

On each server version, it runs three rounds of seven simultaneous rejection
attempts: wrong server name, untrusted server root, expired server, plaintext
endpoint, and missing, untrusted or expired client certificate. Each round waits
for every actor's valid setup before releasing the rejection attempts together.
Probes retain a four-second initial connection budget and a two-second attempt
budget. A separate valid authenticated connection repeatedly sends `Sync` while
rejections run; every actor also checks its valid connection afterward. Setup
and execution have sixteen-second guards, with a ninety-second test guard.
Fixed progress labels and elapsed times identify certificate loading, option
capture, connection completion and disposal without printing secrets.

These short runs do not establish sustained-load or soak behavior. The earlier
intermittent concurrent rejection timeouts have not been reproduced or explained.

The checks exercise trusted connections on every node, digest-only ACLs, node
operations, transactions, one-shot and persistent watch restoration, and
acknowledged close. Negative controls use an expired-certificate server and an
explicit plaintext port. mTLS checks also reject missing, untrusted and expired
client certificates, and distinguish two trusted certificates' X.509 ACL identities.
TLS authentication alone does not grant ZooKeeper permissions.

Generated certificates use a private temporary CA. The tests explicitly select
`X509RevocationMode.NoCheck` because that CA has no revocation service. Hostname
and chain validation remain enabled; application TLS defaults remain unchanged.
The reconnect proxy forwards opaque TLS records without terminating encryption.

The runner prints the evidence directory, containing server versions, certificate
subjects/issuers/dates/fingerprints, configuration, test results and server logs.
Private keys, client/server stores and their password live in a separate private
temporary directory, removed after teardown. The runner scans retained evidence
for private material even on failure; offending files are removed and the run
fails. CI uploads only the evidence directory.
Missing fixture configuration fails selected tests rather than silently skipping.

The plaintext port is solely a no-downgrade negative control. The expired server
uses internal plaintext readiness so certificate rejection is distinguishable
from a stopped server. Quorum traffic between containers is plaintext: this
fixture verifies client TLS, without qualifying quorum TLS, revocation services,
certificate rotation, SASL, Native AOT or production security. Remote CI and
other client operating systems require their own observed results.

The [Apache TLS settings](https://zookeeper.apache.org/doc/r3.9.6/zookeeperAdmin.html#sc_authOptions)
describe the server configuration. macOS imports the test client keys into
temporary platform storage; certificate disposal releases those handles.

See the [connection reference](../reference/connections.md) and
[verification explanation](../explanation/verification.md) for client policy and
what these checks establish.

[How-to guides](README.md) · [Documentation home](../README.md)
