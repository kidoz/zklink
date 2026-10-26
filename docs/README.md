# ZkLink documentation

ZkLink is a managed C# client for Apache ZooKeeper under development, targeting
.NET 10. You can build the library, connect a session, authenticate, sync a path and
close with acknowledgment, and use node/ACL operations and typed transactions.
Active watches remain unimplemented, and no NuGet package is available.

The documentation follows [Diátaxis](https://diataxis.fr/), with four sections
organized around what you need to do.

| Your goal | Start here |
| --- | --- |
| Learn by completing a guided exercise | [Tutorials](tutorials/README.md) |
| Complete a specific development task | [How-to guides](how-to/README.md) |
| Look up types, commands, or current capabilities | [Reference](reference/README.md) |
| Understand the design and verification choices | [Explanation](explanation/README.md) |

New to the repository? Start with [Build and test ZkLink](tutorials/build-and-test.md).
Evaluating it for an application? Check [Current capabilities](reference/current-capabilities.md)
before planning an integration. To exercise the client, follow
[Connect a session](tutorials/connect-a-session.md), then
[Create, read, update and delete a node](tutorials/use-node-operations.md), then
[Execute a transaction](tutorials/use-transactions.md).

## Writing documentation

Choose one primary reader need for each page. Tutorials teach through a fixed
exercise with observable results. How-to guides solve a specific task and assume
the reader knows their goal. Reference pages describe the code and tools.
Explanation pages discuss reasons and tradeoffs. Link between these forms when
readers need more context.

Add each page to its section index. Use relative links for repository files and
run commands from the repository root unless stated otherwise. Check examples
against the current code. Label planned behavior explicitly; keep internal work
tracking and requirement identifiers out of public guides. Add client usage
guides when the relevant behavior can be exercised.

[Project overview](../README.md) · [Changelog](../CHANGELOG.md)
