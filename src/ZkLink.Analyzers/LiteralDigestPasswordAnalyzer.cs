using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace ZkLink.Analyzers;

/// <summary>Reports nonempty compile-time passwords passed to ZooKeeper digest authentication.</summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class LiteralDigestPasswordAnalyzer : DiagnosticAnalyzer
{
    /// <inheritdoc />
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
    [ZkLinkDiagnosticDescriptors.LiteralDigestPassword];

    /// <inheritdoc />
    public override void Initialize(AnalysisContext context)
    {
        if (context is null)
        {
            throw new ArgumentNullException(nameof(context));
        }
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(AnalyzeInvocation, SyntaxKind.InvocationExpression);
    }

    private static void AnalyzeInvocation(SyntaxNodeAnalysisContext context)
    {
        var invocation = (InvocationExpressionSyntax)context.Node;
        if (
            context.SemanticModel.GetSymbolInfo(invocation).Symbol is not IMethodSymbol method
            || !ZkLinkAnalyzerHelpers.IsMethod(
                method,
                ZkLinkAnalyzerHelpers.AuthenticationType,
                "Digest"
            )
        )
        {
            return;
        }

        var arguments = invocation.ArgumentList.Arguments;
        for (var index = 0; index < arguments.Count; index++)
        {
            var argument = arguments[index];
            var isPassword = argument.NameColon is not null
                ? argument.NameColon.Name.Identifier.ValueText == "password"
                : index == 1;
            if (
                isPassword
                && context.SemanticModel.GetConstantValue(argument.Expression)
                    is { HasValue: true } constant
                && constant.Value is string { Length: > 0 }
            )
            {
                context.ReportDiagnostic(
                    Diagnostic.Create(
                        ZkLinkDiagnosticDescriptors.LiteralDigestPassword,
                        argument.Expression.GetLocation()
                    )
                );
            }
        }
    }
}
