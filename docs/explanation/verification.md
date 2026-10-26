# What the verification layers prove

A passing check answers a particular question. Encoding a record correctly,
starting a ZooKeeper ensemble, and recovering a client session require different
evidence. ZkLink keeps those checks separate so progress in one area does not
imply working behavior elsewhere.

## Server-free tests isolate local contracts

The public-contract suite checks ownership, validation, and failure metadata.
The protocol suite checks serialization, malformed input, frame boundaries,
fragmentation, and stream failures. Both run without a ZooKeeper installation, making local
failures repeatable and independent of container readiness.

Virtual-time peers also check handshake/session identity, negotiated liveness,
cancellation outcomes, ordered late replies, bounded admission and close cleanup.
Loopback TCP/TLS tests exercise sockets and certificate validation with ephemeral
certificates. They do not establish recovery against a real quorum or live ACL
and watch semantics. Watch-removal values remain supporting contracts only.

## Java fixtures provide an independent byte comparison

A serializer and deserializer written together can agree on the same mistake.
The optional Java oracle compares C# primitive and record bytes with Apache's
serializer and asks Java to decode the C# output. The downloaded artifact is
pinned by checksum and source build commit.

The fixture covers primitive boundaries, all implemented request/reply shapes,
handshakes, multi records, and Apache path validation for every UTF-16 code unit.
Java verifies emitted C# requests semantically. This does not establish real
operation commitment or supplementary-character interoperability. The
[oracle inventory](../../tests/ZkLink.JavaOracle/README.md) records its exact scope.

## Fixture health establishes a usable test environment

The Compose health probe uses the Java client supplied with each image to perform
an authenticated create/read/delete with disposable credentials and an ephemeral
node. That is stronger readiness evidence than an open TCP port.

The two-version matrix additionally checks the exact server version on every
node, collects logs, and cleans up isolated projects. This makes it useful for
checking the server environment. A successful
run establishes fixture behavior; driver compatibility still needs ZkLink
requests and assertions against both pinned versions.

`just zk-driver-matrix` runs the managed sample against each node: a new session,
digest authentication, sync and acknowledged close. It has passed on both pinned
versions. `just zk-operations-matrix` also runs the live .NET node/ACL/transaction/auxiliary suite and
CRUD, transaction and auxiliary samples. It checks version conflicts, null/empty/large data, create modes,
chroot, ACL enforcement and acknowledged-close ephemeral deletion. Transaction
cases verify complete rollback and independent read errors. A bounded proxy
consumes/drops a real transaction reply, then allows reconnect so tests can check
actual commitment, session identity and exactly one transmitted write. Auxiliary
cases verify textual-prefix session ephemeral queries, chroot, nested counts,
READ permissions and authenticated/anonymous identities. Broader live
recovery, watch delivery and TLS/mTLS ZooKeeper fixtures remain unverified.

## Release claims need consumer evidence

The build workflow defines checks on Linux, Windows, and macOS. A workflow file
records the intended checks; observed runs provide the results. Native AOT needs
another layer: publishing consumers for each declared platform and directly
executing the native output. Package examples must also compile against the
packed release candidate.

Those release checks remain open. The
[capability reference](../reference/current-capabilities.md) separates current
code, recorded checks, and unverified targets. Use
[Run the development checks](../how-to/run-development-checks.md) for executable
commands and [Run a local ZooKeeper ensemble](../how-to/run-local-zookeeper.md)
for fixture operations.

[Explanation](README.md) · [Documentation home](../README.md)
