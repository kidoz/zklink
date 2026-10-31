# ZkLink

A managed asynchronous client for Apache ZooKeeper, targeting .NET 10.
The core has no third-party runtime dependencies.

```sh
dotnet add package ZkLink
```

The client provides one explicit session, node and ACL operations, transactions,
read batches, auxiliary queries and one-shot, persistent and recursive watches.
It reports uncertain request outcomes and reconnect gaps explicitly. TLS is
required by default; plaintext must be explicitly selected for local fixtures.

See the [project documentation](https://github.com/kidoz/zklink/tree/main/docs)
for examples, authentication, session ownership and current verification scope.
The verification matrix covers ZooKeeper 3.8.7 and 3.9.6 on Linux x64/arm64,
Windows x64 and macOS arm64, with managed and native consumers. See the
[release checklist](https://github.com/kidoz/zklink/blob/main/docs/reference/release-readiness.md)
for completed checks, qualification status and platform limits.

The package includes XML API documentation, MIT license and the repository's
third-party notices inventory. Its companion symbol package contains a portable
PDB with Source Link metadata and embedded sources. Embedded sources describe
the exact build, including local working-tree changes.
