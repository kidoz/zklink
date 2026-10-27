using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace ZkLink.Analyzers;

/// <summary>Reports directly unobserved asynchronous client and watch calls.</summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class UnobservedAsyncCallAnalyzer : DiagnosticAnalyzer
{
    /// <inheritdoc />
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
    [ZkLinkDiagnosticDescriptors.UnobservedAsyncCall];

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
        ReportIfUnobserved(context, statement.Expression);
    }

    private static void AnalyzeDiscardAssignment(SyntaxNodeAnalysisContext context)
    {
        var assignment = (AssignmentExpressionSyntax)context.Node;
        if (
            assignment.Left is IdentifierNameSyntax identifier
            && identifier.Identifier.ValueText == "_"
        )
        {
            ReportIfUnobserved(context, assignment.Right);
        }
    }

    private static void ReportIfUnobserved(
        SyntaxNodeAnalysisContext context,
        ExpressionSyntax expression
    )
    {
        var invocation = ZkLinkAnalyzerHelpers.GetDirectInvocation(expression);
        if (
            invocation is null
            || context.SemanticModel.GetSymbolInfo(invocation).Symbol is not IMethodSymbol method
            || !ZkLinkAnalyzerHelpers.IsAsyncMethod(method)
            || ZkLinkAnalyzerHelpers.IsWatchFactory(method)
        )
        {
            return;
        }

        context.ReportDiagnostic(
            Diagnostic.Create(
                ZkLinkDiagnosticDescriptors.UnobservedAsyncCall,
                expression.GetLocation(),
                method.Name
            )
        );
    }
}
