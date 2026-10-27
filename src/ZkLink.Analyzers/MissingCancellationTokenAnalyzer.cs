using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace ZkLink.Analyzers;

/// <summary>Reports async ZkLink calls that omit an available cancellation token.</summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class MissingCancellationTokenAnalyzer : DiagnosticAnalyzer
{
    /// <inheritdoc />
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
    [ZkLinkDiagnosticDescriptors.MissingCancellationToken];

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
            || !ZkLinkAnalyzerHelpers.IsAsyncMethod(method)
            || !AcceptsCancellationToken(method)
            || ArgumentsContainCancellationToken(context, invocation)
            || !IsCancellationTokenAvailableInScope(context, invocation)
        )
        {
            return;
        }

        context.ReportDiagnostic(
            Diagnostic.Create(
                ZkLinkDiagnosticDescriptors.MissingCancellationToken,
                invocation.GetLocation(),
                method.Name
            )
        );
    }

    private static bool AcceptsCancellationToken(IMethodSymbol method)
    {
        foreach (var parameter in method.Parameters)
        {
            if (IsCancellationToken(parameter.Type))
            {
                return true;
            }
        }

        return false;
    }

    private static bool ArgumentsContainCancellationToken(
        SyntaxNodeAnalysisContext context,
        InvocationExpressionSyntax invocation
    )
    {
        foreach (var argument in invocation.ArgumentList.Arguments)
        {
            if (IsCancellationToken(context.SemanticModel.GetTypeInfo(argument.Expression).Type))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsCancellationTokenAvailableInScope(
        SyntaxNodeAnalysisContext context,
        SyntaxNode node
    )
    {
        foreach (var ancestor in node.Ancestors())
        {
            switch (ancestor)
            {
                case MethodDeclarationSyntax method:
                    return HasCancellationTokenParameter(context, method.ParameterList);
                case LocalFunctionStatementSyntax localFunction:
                    if (HasCancellationTokenParameter(context, localFunction.ParameterList))
                    {
                        return true;
                    }

                    break;
                case ParenthesizedLambdaExpressionSyntax lambda:
                    if (HasCancellationTokenParameter(context, lambda.ParameterList))
                    {
                        return true;
                    }

                    break;
            }
        }

        return false;
    }

    private static bool HasCancellationTokenParameter(
        SyntaxNodeAnalysisContext context,
        ParameterListSyntax parameterList
    )
    {
        foreach (var parameter in parameterList.Parameters)
        {
            if (
                context.SemanticModel.GetDeclaredSymbol(parameter) is { Type: { } type }
                && IsCancellationToken(type)
            )
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsCancellationToken(ITypeSymbol? type) =>
        type?.ToDisplayString() == "System.Threading.CancellationToken";
}
