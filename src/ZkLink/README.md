# ZkLink

A managed asynchronous client for Apache ZooKeeper, targeting .NET 10.
The core has no third-party runtime dependencies.

Version 0.1.0 is the initial core release. The API remains under development and
may change before 1.0; Native AOT support is not advertised.

```sh
dotnet add package ZkLink --version 0.1.0
```

The client provides one explicit session, node and ACL operations, transactions,
read batches, auxiliary queries and one-shot, persistent and recursive watches.
It reports uncertain request outcomes and reconnect gaps explicitly. TLS is
required by default; plaintext must be explicitly selected for local fixtures.

See the [project documentation](https://github.com/kidoz/zklink/tree/main/docs)
for examples, authentication, session ownership and current verification scope.
Local managed checks cover ZooKeeper 3.8.7 and 3.9.6; broader server and platform
qualification remains open.

The package includes XML API documentation, MIT license and the repository's
third-party notices inventory. Its companion symbol package contains a portable
PDB with Source Link metadata and embedded sources. Embedded sources describe
the exact build, including local working-tree changes.
