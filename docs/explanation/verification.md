# What the verification layers prove

A passing check answers a particular question. Encoding a record correctly,
starting a ZooKeeper ensemble, and recovering a client session require different
evidence. ZkLink keeps those checks separate so progress in one area does not
imply working behavior elsewhere.

## Server-free tests isolate local contracts

The public-contract suite checks ownership, validation, and failure metadata.
The protocol suite checks serialization, malformed input, frame boundaries,
fragmentation, and stream failures. Both run without a server, making local
failures repeatable and independent of container readiness.

These tests cannot establish handshakes, reconnect behavior, request ordering,
or watch delivery. Those systems are not implemented yet. Similarly, a watch
removal result can preserve its status correctly without any watch having been
registered or removed.

## Java fixtures provide an independent byte comparison

A serializer and deserializer written together can agree on the same mistake.
The optional Java oracle compares C# primitive and record bytes with Apache's
serializer and asks Java to decode the C# output. The downloaded artifact is
pinned by checksum and source build commit.

The fixture covers selected boundaries, null and empty values, non-surrogate
BMP strings, Stat, and headers. It does not cover handshakes, opcode dispatch,
multi records, or live operations. Apache's writer and the strict C# UTF-8 codec
also handle surrogate code units differently; this fixture makes no
supplementary-character interoperability claim. The
[oracle inventory](../../tests/ZkLink.JavaOracle/README.md) records its exact scope.

## Fixture health establishes a usable test environment

The Compose health probe uses the Java client supplied with each image to perform
an authenticated create/read/delete with disposable credentials and an ephemeral
node. That is stronger readiness evidence than an open TCP port.

The two-version matrix additionally checks the exact server version on every
node, collects logs, and cleans up isolated projects. This makes it useful for
checking the environment before driver integration tests exist. A successful
run establishes fixture behavior; driver compatibility still needs ZkLink
requests and assertions against both pinned versions.

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
