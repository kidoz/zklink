using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace ZkLink.Analyzers;

internal static class ZkLinkAnalyzerHelpers
{
    internal const string ClientType = "ZkLink.ZooKeeperClient";
    internal const string WatchType = "ZkLink.ZooKeeperWatch";
    internal const string AuthenticationType = "ZkLink.ZooKeeperAuthentication";
    internal const string OptionsType = "ZkLink.ZooKeeperClientOptions";

    internal static bool IsMethod(IMethodSymbol method, string containingType, string name) =>
        method.Name == name && method.ContainingType.ToDisplayString() == containingType;

    internal static bool IsZkLinkType(ITypeSymbol? type, string expectedType) =>
        type?.ToDisplayString() == expectedType;

    internal static bool IsAsyncMethod(IMethodSymbol method) =>
        (
            method.ContainingType.ToDisplayString() == ClientType
            || method.ContainingType.ToDisplayString() == WatchType
        )
        && method.Name.EndsWith("Async", StringComparison.Ordinal)
        && IsTaskLike(method.ReturnType);

    internal static bool IsWatchFactory(IMethodSymbol method) =>
        method.ContainingType.ToDisplayString() == ClientType
        && (
            method.Name == "AddWatchAsync"
            || method.Name == "ExistsAndWatchAsync"
            || method.Name == "GetDataAndWatchAsync"
            || method.Name == "GetChildrenAndWatchAsync"
        );

    internal static InvocationExpressionSyntax? GetDirectInvocation(ExpressionSyntax expression)
    {
        while (expression is ParenthesizedExpressionSyntax parenthesized)
        {
            expression = parenthesized.Expression;
        }

        return expression as InvocationExpressionSyntax;
    }

    internal static InvocationExpressionSyntax? GetWatchInvocation(ExpressionSyntax expression)
    {
        if (expression is AwaitExpressionSyntax awaited)
        {
            expression = awaited.Expression;
        }

        return GetDirectInvocation(expression);
    }

    private static bool IsTaskLike(ITypeSymbol type) =>
        (type.Name == "Task" || type.Name == "ValueTask")
        && type.ContainingNamespace.ToDisplayString() == "System.Threading.Tasks";
}
