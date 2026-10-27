using Microsoft.CodeAnalysis;

namespace ZkLink.Analyzers;

internal static class ZkLinkDiagnosticDescriptors
{
    internal static readonly DiagnosticDescriptor LiteralDigestPassword = new(
        "ZKL0001",
        "Avoid literal ZooKeeper digest passwords",
        "Digest password is a compile-time literal; load credentials from configuration or a secret store",
        "Security",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Hard-coded credentials can leak through source history. The diagnostic never includes the credential value."
    );

    internal static readonly DiagnosticDescriptor UnsafePlaintextDigest = new(
        "ZKL0002",
        "Avoid digest authentication over plaintext",
        "AllowUnsafePlaintextDigest is explicitly enabled; use TLS except for controlled development fixtures",
        "Security",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Digest credentials require transport protection outside an explicit local development boundary."
    );

    internal static readonly DiagnosticDescriptor DiscardedWatch = new(
        "ZKL0003",
        "Retain and own ZooKeeper watch results",
        "Watch-producing call '{0}' has its result discarded; retain and dispose the watch or observed result",
        "Usage",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Watch handles own registration lifetime and buffered events. Discarding them can silently lose ownership and delivery."
    );

    internal static readonly DiagnosticDescriptor UnobservedAsyncCall = new(
        "ZKL0004",
        "Observe ZkLink asynchronous calls",
        "Asynchronous call '{0}' is not awaited or otherwise observed",
        "Usage",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Unobserved operations can lose completion, failure and outcome information. Await the operation or explicitly observe its task."
    );

    internal static readonly DiagnosticDescriptor MissingCancellationToken = new(
        "ZKL0005",
        "Pass an available CancellationToken to ZkLink async calls",
        "Async ZkLink call '{0}' does not pass the CancellationToken available in scope",
        "Usage",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Pass the enclosing cancellation token so the operation observes application shutdown and caller cancellation."
    );

    internal static readonly DiagnosticDescriptor SyncOverAsync = new(
        "ZKL0006",
        "Avoid blocking on ZkLink async operations",
        "Blocking on async ZkLink call '{0}' with '{1}' can deadlock; await it instead",
        "Usage",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Blocking on network operations can exhaust threads and deadlock. Await the operation instead."
    );
}
