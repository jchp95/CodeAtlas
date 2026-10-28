using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

/// <summary>Task.Result / Task.Wait() / GetAwaiter().GetResult(): synchronous blocking on asynchronous work.</summary>
sealed class BlockingOnAsyncRule : ICodeRule
{
    public RuleDescriptor Descriptor { get; } = new("ATL1001", "BlockingOnAsync", "async",
        FindingSeverity.Warning, "Synchronous wait on a Task (.Result, .Wait(), .GetAwaiter().GetResult()).");

    public void Analyze(RuleContext context)
    {
        foreach (var node in context.Root.DescendantNodes())
        {
            context.Cancellation.ThrowIfCancellationRequested();
            switch (node)
            {
                case MemberAccessExpressionSyntax { Name.Identifier.ValueText: "Result" } access
                    when context.Model.GetSymbolInfo(access, context.Cancellation).Symbol is IPropertySymbol property
                         && property.ContainingType.OriginalDefinition.IsTaskLike():
                    Report(context, access, access.Expression, $"{property.ContainingType.Name}.Result");
                    break;

                case InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax access } invocation
                    when context.Model.GetSymbolInfo(invocation, context.Cancellation).Symbol is IMethodSymbol method && IsBlockingMethod(method):
                    Report(context, invocation, access.Expression, $"{method.ContainingType.Name}.{method.Name}()");
                    break;
            }
        }
    }

    private static bool IsBlockingMethod(IMethodSymbol method)
    {
        var container = method.ContainingType;
        var ns = container.ContainingNamespace?.ToDisplayString();
        if (ns == "System.Threading.Tasks" && container.Name == "Task" && method.Name is "Wait" or "WaitAll" or "WaitAny") return true;
        return ns == "System.Runtime.CompilerServices" && method.Name == "GetResult" && container.Name.EndsWith("Awaiter", StringComparison.Ordinal);
    }

    private void Report(RuleContext context, SyntaxNode node, ExpressionSyntax receiver, string api)
    {
        var member = node.EnclosingDeclaredMember();
        var symbol = member is null ? null : context.Model.GetDeclaredSymbol(member, context.Cancellation) as IMethodSymbol;
        var inAsyncContext = symbol is not null && (symbol.IsAsync || symbol.ReturnType.IsTaskLike());

        // A task that was provably awaited earlier (e.g. after Task.WhenAll) is already complete; blocking on it is harmless.
        if (member is not null && IsAlreadyAwaited(context, member, node, receiver)) return;

        context.Report(node,
            $"Synchronous wait on a Task via {api}",
            $"{api} blocks the calling thread until the task completes.",
            ValidationLevel.StaticProven,
            inAsyncContext ? "high" : "medium",
            new[]
            {
                context.Evidence("blocking-call", $"{api} is invoked on a Task-typed expression (resolved by the semantic model).", node),
                new FindingEvidence("context", inAsyncContext
                    ? $"The call is inside {symbol!.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat)}, which is asynchronous or returns a Task."
                    : "The call is in a synchronous member; blocking may be intentional (for example at a process entry point).")
            },
            "Under a synchronization context this can deadlock; on the thread pool it holds a thread for the whole wait and contributes to thread-pool starvation. " +
            "Neither effect has been measured here.",
            "Make the member asynchronous and use 'await'. If the call must stay synchronous, document why and prefer GetAwaiter().GetResult() over .Result/.Wait() so exceptions are not wrapped in AggregateException.",
            "Confirm with a trace: capture dotnet-counters ThreadPool Queue Length / Thread Count while the code path runs under load, or add a test that runs the path inside a single-threaded SynchronizationContext.",
            inAsyncContext
                ? new[] { "The pattern is proven; deadlock or starvation depends on the runtime synchronization context and load, which static analysis cannot observe." }
                : new[] { "The pattern is proven; whether it is harmful in a synchronous entry point depends on the host.", "Not flagged as high confidence because the enclosing member is synchronous." },
            inAsyncContext ? FindingSeverity.Warning : FindingSeverity.Info);
    }

    private static bool IsAlreadyAwaited(RuleContext context, SyntaxNode member, SyntaxNode usage, ExpressionSyntax receiver)
    {
        if (receiver is not IdentifierNameSyntax identifier) return false;
        var symbol = context.Model.GetSymbolInfo(identifier, context.Cancellation).Symbol;
        if (symbol is null) return false;

        foreach (var await in member.DescendantNodes().OfType<AwaitExpressionSyntax>().Where(a => a.SpanStart < usage.SpanStart))
        {
            var operand = await.Expression;
            if (operand is IdentifierNameSyntax awaited &&
                SymbolEqualityComparer.Default.Equals(context.Model.GetSymbolInfo(awaited, context.Cancellation).Symbol, symbol)) return true;

            if (operand is InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax { Name.Identifier.ValueText: "WhenAll" or "WhenAny" } } combinator &&
                combinator.ArgumentList.Arguments.Any(argument => argument.Expression is IdentifierNameSyntax arg &&
                    SymbolEqualityComparer.Default.Equals(context.Model.GetSymbolInfo(arg, context.Cancellation).Symbol, symbol))) return true;
        }
        return false;
    }
}
