using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

/// <summary>A CancellationToken is available but a callee that could accept it is called without it.</summary>
sealed class CancellationPropagationRule : ICodeRule
{
    public RuleDescriptor Descriptor { get; } = new("ATL1003", "CancellationNotPropagated", "async",
        FindingSeverity.Warning, "A CancellationToken in scope is not passed to a callee that accepts one.");

    public void Analyze(RuleContext context)
    {
        foreach (var invocation in context.Root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            context.Cancellation.ThrowIfCancellationRequested();
            var token = FindTokenInScope(context, invocation);
            if (token is null) continue;
            if (context.Model.GetOperation(invocation, context.Cancellation) is not IInvocationOperation operation) continue;
            var target = operation.TargetMethod;
            if (target.MethodKind == MethodKind.DelegateInvoke) continue;
            if (operation.Arguments.Any(a => a.ArgumentKind != ArgumentKind.DefaultValue && a.Value.Type.IsCancellationToken())) continue;

            string? how = null;
            if (operation.Arguments.Any(a => a.ArgumentKind == ArgumentKind.DefaultValue && a.Parameter?.Type.IsCancellationToken() == true))
                how = $"'{target.Name}' has an optional CancellationToken parameter that was left at its default.";
            else if (FindOverloadWithToken(target) is { } overload)
                how = $"'{target.ContainingType.Name}.{target.Name}' has an overload that also accepts a CancellationToken: {overload.ToDisplayString(SymbolDisplayFormat.CSharpShortErrorMessageFormat)}.";
            if (how is null) continue;

            var args = invocation.ArgumentList;
            var hasNamedArgument = args.Arguments.Any(a => a.NameColon is not null);
            SuggestedFix? fix = null;
            if (!hasNamedArgument)
            {
                var insertion = (args.Arguments.Count == 0 ? "" : ", ") + token.Name;
                fix = context.FixFor($"Pass '{token.Name}' to {target.Name}", new TextEdit(context.RelativeFile, args.CloseParenToken.SpanStart, 0, insertion));
            }

            context.Report(invocation,
                $"CancellationToken '{token.Name}' is not passed to {target.Name}",
                "Cancellation requested by the caller will not interrupt this operation.",
                ValidationLevel.StaticProven, "high",
                new[]
                {
                    context.Evidence("call", $"{target.ToDisplayString(SymbolDisplayFormat.CSharpShortErrorMessageFormat)} is called without a token.", invocation),
                    new FindingEvidence("token-in-scope", $"Parameter '{token.Name}' (CancellationToken) is available in the enclosing member."),
                    new FindingEvidence("callee", how)
                },
                "A cancelled request keeps running this operation to completion, holding threads, connections and time until it finishes on its own.",
                $"Pass '{token.Name}' to the call. If detaching from cancellation is deliberate, pass CancellationToken.None explicitly and add a comment.",
                "Start the operation, cancel the token while the callee is running, and assert that it completes with OperationCanceledException instead of running to the end.",
                new[] { "Intentional non-cancellation (for example cleanup that must finish) cannot be inferred from the code." },
                fix: fix,
                discriminator: invocation.ToString());
        }
    }

    private static IParameterSymbol? FindTokenInScope(RuleContext context, SyntaxNode node)
    {
        var symbol = context.Model.GetEnclosingSymbol(node.SpanStart, context.Cancellation);
        while (symbol is not null)
        {
            if (symbol is IMethodSymbol method)
            {
                var token = method.Parameters.FirstOrDefault(p => p.Type.IsCancellationToken());
                if (token is not null) return token;
            }
            symbol = symbol.ContainingSymbol;
            if (symbol is INamedTypeSymbol) break;
        }
        return null;
    }

    private static IMethodSymbol? FindOverloadWithToken(IMethodSymbol target)
    {
        var original = target.ReducedFrom ?? target;
        var expected = original.Parameters.Select(p => p.Type.ToDisplayString()).ToArray();
        return original.ContainingType.GetMembers(original.Name).OfType<IMethodSymbol>()
            .Where(m => m.IsStatic == original.IsStatic && m.Parameters.Length == expected.Length + 1 && m.Parameters[^1].Type.IsCancellationToken())
            .FirstOrDefault(m => m.Parameters.Take(expected.Length).Select(p => p.Type.ToDisplayString()).SequenceEqual(expected));
    }
}
