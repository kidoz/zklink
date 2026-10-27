# ZkLink.Analyzers

Roslyn diagnostics for secure and asynchronous ZkLink usage. The analyzer targets `netstandard2.0` and has no runtime dependency on the ZkLink client.

## Rules

| ID | Category | Default | Meaning |
| --- | --- | --- | --- |
| ZKL0001 | Security | Warning | A nonempty compile-time password is passed to `ZooKeeperAuthentication.Digest`. Load secrets from configuration or a secret store. |
| ZKL0002 | Security | Warning | `AllowUnsafePlaintextDigest = true` appears in client options. Use TLS outside controlled local development. |
| ZKL0003 | Usage | Warning | A watch-producing call has its result discarded. Retain and dispose the watch or watched result. |
| ZKL0004 | Usage | Warning | A client or watch `Task`/`ValueTask` call is not awaited or otherwise observed. |
| ZKL0005 | Usage | Warning | An async call omits a `CancellationToken` available in the enclosing scope. Passing `default` explicitly is allowed. |
| ZKL0006 | Usage | Warning | A direct ZkLink async call is blocked with `.Result`, `.Wait()`, or `.GetAwaiter().GetResult()`. Await it instead. |

Diagnostics use ZkLink symbol matching and skip generated code. They intentionally check direct source patterns rather than interprocedural flow. Runtime validation remains authoritative.

No code fixes or automatic retries are supplied. Credential storage, watch ownership, cancellation, and uncertain request outcomes require application-specific decisions.

## Use from this source checkout

Reference the analyzer without loading it as a runtime library:

```xml
<ItemGroup>
  <ProjectReference
    Include="path/to/src/ZkLink.Analyzers/ZkLink.Analyzers.csproj"
    OutputItemType="Analyzer"
    ReferenceOutputAssembly="false" />
</ItemGroup>
```

The local package smoke test packs `0.0.0-local`, restores a consumer from that package, compiles clean code, and verifies an intentional unobserved call reports ZKL0004:

```sh
bash dev/test-analyzers-package.sh
```

To escalate a rule in a consuming project:

```ini
dotnet_diagnostic.ZKL0004.severity = error
```

The project is under development. No analyzer NuGet release, cross-platform package qualification, or Native AOT support is claimed.
