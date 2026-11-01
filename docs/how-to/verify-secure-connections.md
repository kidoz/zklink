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
controls run independently. Functional negative probes have an eight-second
initial and attempt budget with the same rejection assertions and outer timeout
guards; sustained concurrent
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

## Required authentication policies

Run the separate authentication checks with:

```sh
just zk-authentication
```

This command needs the pinned .NET SDK, Docker with Compose and Python 3. It
starts separate digest-required and SASL-required three-node fixtures for both
ZooKeeper versions. Every member must pass authenticated Java create/read/delete
readiness, and its version and leader/follower role are recorded.

Digest checks use two generated UTF-8 credentials, verify both identities in
creator ACLs, and read the protected node with each identity separately.
Reconnect must replay both credentials before protected watch restoration and
readiness. A wrong password produces per-operation `NoAuth`; the session remains
usable. A client without the required authentication becomes terminal `AuthFailed`.

SASL remains unsupported by ZkLink. ZooKeeper accepts the connection handshake
and digest authentication packets before enforcing its required scheme on an
application operation. The checks require that operation to fail with
`Rejected` and `SessionClosedRequiresSaslAuth`, followed by terminal `AuthFailed`,
closed admission and no automatic replacement. The same server error code also
applies to an unauthenticated client on a digest-required server; its legacy
name does not identify the configured scheme.

The Java positive control uses a private SASL DIGEST-MD5 login. This disposable
SASL fixture explicitly disables ZooKeeper's FIPS compatibility mode on its
servers and Java clients because that mode disables the legacy mechanism.
Digest fixtures retain each server version's default. See the
[ZooKeeper authentication and FIPS settings](https://zookeeper.apache.org/doc/r3.9.6/zookeeperAdmin.html).
These fixtures verify managed digest behavior and rejection of unsupported
authentication; they do not establish SASL or FIPS support in ZkLink.

JAAS credentials and test passwords live in a private directory outside retained
evidence. Fresh authenticated probes run before teardown, including after test
failure. A final scan fails and redacts evidence if private values appear, then
removes the private directory. Configuration, roles, test results and server logs
remain available for review. Missing fixture configuration fails selected cases
before opening a client.

All 24 live policy cases pass locally on macOS arm64 and in
[Linux verification](https://github.com/kidoz/zklink/actions/runs/41286490991).
That run also passes the existing 50 operations cases per server version and
the 10 controlled authentication cases on each of four managed targets.

## Online revocation and connection cleanup

Run the platform revocation checks without Docker or a ZooKeeper installation:

```sh
dotnet test --project tests/ZkLink.Protocol.Tests -c Release --filter-class '*RevocationTests'
```

These tests use real `SslStream` connections, generated private issuers and
loopback CRL/OCSP responders. They verify the default `Online` policy: a valid
status allows a ZooKeeper handshake, sync and acknowledged close; revoked,
unavailable and incorrectly signed status responses fail before ZooKeeper
application bytes are sent. Explicit `NoCheck` controls establish that chain,
name and protocol setup are otherwise valid. Each case has a separate issuer,
TLS identity and responder URL; macOS also uses separate loopback responder
hostnames to isolate platform caches. No root is installed in a system store.

Held-response tests cancel the caller or advance the driver deadline after a
status lookup starts, then require failure and owned cleanup without a ready
client. The driver sets the platform's certificate URL retrieval timeout to
`ConnectAttemptTimeout` and rechecks cancellation before transferring a
completed transport. Online checking and hostname/trust validation remain
enabled.

Certificate URL timeouts apply to individual downloads, not necessarily the
whole chain. Several OCSP, CRL or issuer downloads can extend native validation
past the requested connection deadline. Cancellation completion waits for that
work and owned cleanup. macOS ignores `UrlRetrievalTimeout`; the OS controls its
retrieval behavior. The tests supply OCSP as well as CRLs because CRL-only
private-chain revocation is not qualified on macOS. An unavailable status
remains a failure; the driver does not switch to `NoCheck` or plaintext.

These controlled-peer checks qualify the observed managed-client behavior.
The native consumer also runs valid, revoked, unavailable, invalid-signature and
held-cancellation cases with independent `NoCheck` controls:

```sh
python3 dev/check-native-revocation.py artifacts/aot/run.<id>/native.json
```

The [shared-candidate run](https://github.com/kidoz/zklink/actions/runs/41239985235)
passes all five native cases on Linux x64/arm64, Windows x64 and macOS arm64.
The separate two-version ZooKeeper fixtures still use `NoCheck`; production
revocation services, multi-issuer deployments and certificate rotation require
their own evidence. The local macOS 27 host currently reports unknown status
without contacting the loopback responder while a VPN, proxy or network filter
is active. Its cause is unconfirmed; network settings and client policy remain
unchanged.

See Microsoft's [certificate URL timeout API](https://learn.microsoft.com/dotnet/api/system.security.cryptography.x509certificates.x509chainpolicy.urlretrievaltimeout)
and [platform cryptography limits](https://learn.microsoft.com/dotnet/standard/security/cross-platform-cryptography#x509chain),
and the [.NET 10.0.14 private-chain revocation tests](https://github.com/dotnet/runtime/blob/v10.0.14/src/libraries/System.Security.Cryptography/tests/X509Certificates/RevocationTests/DynamicRevocationTests.cs).

The [Apache TLS settings](https://zookeeper.apache.org/doc/r3.9.6/zookeeperAdmin.html#sc_authOptions)
describe the server configuration. macOS imports the test client keys into
temporary platform storage; certificate disposal releases those handles.

See the [connection reference](../reference/connections.md) and
[verification explanation](../explanation/verification.md) for client policy and
what these checks establish.

[How-to guides](README.md) · [Documentation home](../README.md)
