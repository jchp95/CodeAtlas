using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

/// <summary>
/// A network or database call executed once per loop iteration. The loop bound and the call cost are unknown
/// statically, so this is reported as a risk (N+1 candidate), never as a demonstrated performance defect.
/// </summary>
sealed class RemoteCallInLoopRule : ICodeRule
{
    public RuleDescriptor Descriptor { get; } = new("ATL1007", "RemoteCallInLoop", "performance",
        FindingSeverity.Warning, "Network or database call executed on every loop iteration (N+1 candidate).");

    public void Analyze(RuleContext context)
    {
        foreach (var invocation in context.Root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            context.Cancellation.ThrowIfCancellationRequested();
            var loop = invocation.Ancestors().TakeWhile(a => a is not (BaseMethodDeclarationSyntax or AccessorDeclarationSyntax or AnonymousFunctionExpressionSyntax or LocalFunctionStatementSyntax))
                .FirstOrDefault(a => a is ForStatementSyntax or ForEachStatementSyntax or WhileStatementSyntax or DoStatementSyntax);
            if (loop is null) continue;
            if (context.Model.GetSymbolInfo(invocation, context.Cancellation).Symbol is not IMethodSymbol method) continue;
            if (ExternalApiClassifier.Remote(method) is not { } kind) continue;

            // The collection of a foreach and the initializer of a for are evaluated once, not per iteration.
            var inHeader = loop switch
            {
                ForEachStatementSyntax f => f.Expression.Span.Contains(invocation.Span),
                ForStatementSyntax f => (f.Declaration?.Span.Contains(invocation.Span) ?? false)
                    || f.Initializers.Any(i => i.Span.Contains(invocation.Span)),
                _ => false
            };
            if (inHeader) continue;

            var loopLine = context.Tree.GetLineSpan(loop.Span).StartLinePosition.Line + 1;
            context.Report(invocation,
                $"{kind} call '{method.Name}' inside a loop",
                $"{method.ContainingType.Name}.{method.Name} is executed on each iteration of the enclosing {LoopName(loop)} loop (line {loopLine}).",
                ValidationLevel.StaticRisk, "medium",
                new[]
                {
                    context.Evidence("call", $"{method.ContainingType.ToDisplayString(SymbolDisplayFormat.CSharpShortErrorMessageFormat)}.{method.Name} resolves to a {kind.ToLowerInvariant()} operation.", invocation),
                    new FindingEvidence("loop", $"Enclosing {LoopName(loop)} loop starts at line {loopLine}.", context.LocationOf(loop))
                },
                "If the loop is long, latency and load scale with the iteration count (one round trip each). Actual iteration counts and call cost were not measured.",
                "Batch the work: fetch all needed rows with one query (Where(ids.Contains)), use a bulk API, or run bounded concurrent requests. Keep the loop only if the operations are inherently sequential.",
                "Count calls per request with OpenTelemetry/dotnet-trace (HTTP client or EF command events) for a realistic input size, or add a test double that counts invocations and assert it does not grow with the input.",
                new[] { "Loop bounds are unknown statically.", "A deliberately sequential or throttled loop is not distinguishable from an accidental N+1.", "Database N+1 is not confirmed without the provider, the query plan or command logs." },
                discriminator: invocation.ToString());
        }
    }

    private static string LoopName(SyntaxNode loop) => loop switch
    {
        ForEachStatementSyntax => "foreach",
        ForStatementSyntax => "for",
        WhileStatementSyntax => "while",
        _ => "do-while"
    };
}
