using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace ZkLink.Analyzers;

/// <summary>Reports directly discarded watch-producing calls.</summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class DiscardedWatchAnalyzer : DiagnosticAnalyzer
{
    /// <inheritdoc />
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
    [ZkLinkDiagnosticDescriptors.DiscardedWatch];

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
        ReportIfWatch(context, statement.Expression);
    }

    private static void AnalyzeDiscardAssignment(SyntaxNodeAnalysisContext context)
    {
        var assignment = (AssignmentExpressionSyntax)context.Node;
        if (
            assignment.Left is IdentifierNameSyntax identifier
            && identifier.Identifier.ValueText == "_"
        )
        {
            ReportIfWatch(context, assignment.Right);
        }
    }

    private static void ReportIfWatch(
        SyntaxNodeAnalysisContext context,
        ExpressionSyntax expression
    )
    {
        var invocation = ZkLinkAnalyzerHelpers.GetWatchInvocation(expression);
        if (
            invocation is null
            || context.SemanticModel.GetSymbolInfo(invocation).Symbol is not IMethodSymbol method
            || !ZkLinkAnalyzerHelpers.IsWatchFactory(method)
        )
        {
            return;
        }

        context.ReportDiagnostic(
            Diagnostic.Create(
                ZkLinkDiagnosticDescriptors.DiscardedWatch,
                expression.GetLocation(),
                method.Name
            )
        );
    }
}
