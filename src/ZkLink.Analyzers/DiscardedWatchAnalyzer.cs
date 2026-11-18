using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace ZkLink.Analyzers;

/// <summary>Reports directly discarded watches and state subscriptions.</summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class DiscardedWatchAnalyzer : DiagnosticAnalyzer
{
    /// <inheritdoc />
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
    [
        ZkLinkDiagnosticDescriptors.DiscardedWatch,
        ZkLinkDiagnosticDescriptors.DiscardedStateSubscription,
    ];

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
            AnalyzeExpressionStatement,
            SyntaxKind.ExpressionStatement
        );
        context.RegisterSyntaxNodeAction(
            AnalyzeDiscardAssignment,
            SyntaxKind.SimpleAssignmentExpression
        );
    }

    private static void AnalyzeExpressionStatement(SyntaxNodeAnalysisContext context)
    {
        var statement = (ExpressionStatementSyntax)context.Node;
        ReportDiscard(context, statement.Expression);
    }

    private static void AnalyzeDiscardAssignment(SyntaxNodeAnalysisContext context)
    {
        var assignment = (AssignmentExpressionSyntax)context.Node;
        if (
            assignment.Left is IdentifierNameSyntax identifier
            && identifier.Identifier.ValueText == "_"
            && context.SemanticModel.GetOperation(identifier, context.CancellationToken)
                is IDiscardOperation
        )
        {
            ReportDiscard(context, assignment.Right);
        }
    }

    private static void ReportDiscard(
        SyntaxNodeAnalysisContext context,
        ExpressionSyntax expression
    )
    {
        var invocation = ZkLinkAnalyzerHelpers.GetWatchInvocation(expression);
        if (
            invocation is null
            || context.SemanticModel.GetSymbolInfo(invocation).Symbol is not IMethodSymbol method
        )
        {
            return;
        }

        if (ZkLinkAnalyzerHelpers.IsWatchFactory(method))
        {
            context.ReportDiagnostic(
                Diagnostic.Create(
                    ZkLinkDiagnosticDescriptors.DiscardedWatch,
                    expression.GetLocation(),
                    method.Name
                )
            );
        }
        else if (
            ZkLinkAnalyzerHelpers.IsMethod(
                method,
                ZkLinkAnalyzerHelpers.ClientType,
                "SubscribeStateChanges"
            )
        )
        {
            context.ReportDiagnostic(
                Diagnostic.Create(
                    ZkLinkDiagnosticDescriptors.DiscardedStateSubscription,
                    expression.GetLocation()
                )
            );
        }
    }
}
