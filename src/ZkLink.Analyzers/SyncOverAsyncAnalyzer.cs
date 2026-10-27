using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace ZkLink.Analyzers;

/// <summary>Reports direct sync-over-async patterns on ZkLink operations.</summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class SyncOverAsyncAnalyzer : DiagnosticAnalyzer
{
    /// <inheritdoc />
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
    [ZkLinkDiagnosticDescriptors.SyncOverAsync];

    /// <inheritdoc />
    public override void Initialize(AnalysisContext context)
    {
        if (context is null)
        {
            throw new ArgumentNullException(nameof(context));
        }

        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(
            AnalyzeMemberAccess,
            SyntaxKind.SimpleMemberAccessExpression
        );
        context.RegisterSyntaxNodeAction(AnalyzeInvocation, SyntaxKind.InvocationExpression);
    }

    private static void AnalyzeMemberAccess(SyntaxNodeAnalysisContext context)
    {
        var memberAccess = (MemberAccessExpressionSyntax)context.Node;
        if (
            memberAccess.Name.Identifier.ValueText == "Result"
            && TryGetAsyncCallName(context, memberAccess.Expression) is { } methodName
        )
        {
            Report(context, memberAccess, methodName, ".Result");
        }
    }

    private static void AnalyzeInvocation(SyntaxNodeAnalysisContext context)
    {
        var invocation = (InvocationExpressionSyntax)context.Node;
        if (invocation.Expression is not MemberAccessExpressionSyntax memberAccess)
        {
            return;
        }

        var memberName = memberAccess.Name.Identifier.ValueText;
        if (
            memberName == "Wait"
            && TryGetAsyncCallName(context, memberAccess.Expression) is { } waitTarget
        )
        {
            Report(context, invocation, waitTarget, ".Wait()");
            return;
        }

        if (
            memberName == "GetResult"
            && memberAccess.Expression is InvocationExpressionSyntax awaiterInvocation
            && awaiterInvocation.Expression is MemberAccessExpressionSyntax awaiterAccess
            && awaiterAccess.Name.Identifier.ValueText == "GetAwaiter"
            && TryGetAsyncCallName(context, awaiterAccess.Expression) is { } awaiterTarget
        )
        {
            Report(context, invocation, awaiterTarget, ".GetAwaiter().GetResult()");
        }
    }

    private static string? TryGetAsyncCallName(
        SyntaxNodeAnalysisContext context,
        ExpressionSyntax expression
    )
    {
        var invocation = ZkLinkAnalyzerHelpers.GetDirectInvocation(expression);
        return
            invocation is not null
            && context.SemanticModel.GetSymbolInfo(invocation).Symbol is IMethodSymbol method
            && ZkLinkAnalyzerHelpers.IsAsyncMethod(method)
            ? method.Name
            : null;
    }

    private static void Report(
        SyntaxNodeAnalysisContext context,
        SyntaxNode node,
        string methodName,
        string blockingMember
    ) =>
        context.ReportDiagnostic(
            Diagnostic.Create(
                ZkLinkDiagnosticDescriptors.SyncOverAsync,
                node.GetLocation(),
                methodName,
                blockingMember
            )
        );
}
