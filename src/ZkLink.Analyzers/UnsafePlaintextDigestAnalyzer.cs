using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace ZkLink.Analyzers;

/// <summary>Reports explicit digest-over-plaintext opt-in in client options.</summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class UnsafePlaintextDigestAnalyzer : DiagnosticAnalyzer
{
    /// <inheritdoc />
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
    [ZkLinkDiagnosticDescriptors.UnsafePlaintextDigest];

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
            AnalyzeObjectCreation,
            SyntaxKind.ObjectCreationExpression,
            SyntaxKind.ImplicitObjectCreationExpression
        );
    }

    private static void AnalyzeObjectCreation(SyntaxNodeAnalysisContext context)
    {
        var creation = (BaseObjectCreationExpressionSyntax)context.Node;
        if (
            !ZkLinkAnalyzerHelpers.IsZkLinkType(
                context.SemanticModel.GetTypeInfo(creation).Type,
                ZkLinkAnalyzerHelpers.OptionsType
            )
        )
        {
            return;
        }

        foreach (
            var assignment in creation.Initializer?.Expressions.OfType<AssignmentExpressionSyntax>()
                ?? []
        )
        {
            if (
                context.SemanticModel.GetSymbolInfo(assignment.Left).Symbol
                    is not IPropertySymbol property
                || property.Name != "AllowUnsafePlaintextDigest"
                || property.ContainingType.ToDisplayString() != ZkLinkAnalyzerHelpers.OptionsType
            )
            {
                continue;
            }

            var value = context.SemanticModel.GetConstantValue(assignment.Right);
            if (value is { HasValue: true, Value: true })
            {
                context.ReportDiagnostic(
                    Diagnostic.Create(
                        ZkLinkDiagnosticDescriptors.UnsafePlaintextDigest,
                        assignment.Right.GetLocation()
                    )
                );
            }
        }
    }
}
