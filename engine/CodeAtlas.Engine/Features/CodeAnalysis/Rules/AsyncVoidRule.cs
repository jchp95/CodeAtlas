using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

/// <summary>async void methods that are not event handlers: exceptions cannot be observed by the caller.</summary>
sealed class AsyncVoidRule : ICodeRule
{
    public RuleDescriptor Descriptor { get; } = new("ATL1002", "AsyncVoid", "async",
        FindingSeverity.Warning, "'async void' method that is not an event handler.");

    public void Analyze(RuleContext context)
    {
        foreach (var method in context.Root.DescendantNodes().OfType<MethodDeclarationSyntax>())
        {
            context.Cancellation.ThrowIfCancellationRequested();
            if (!method.Modifiers.Any(SyntaxKind.AsyncKeyword)) continue;
            if (context.Model.GetDeclaredSymbol(method, context.Cancellation) is not { ReturnsVoid: true } symbol) continue;
            if (IsEventHandlerShape(symbol)) continue;

            context.Report(method.Identifier.Parent!,
                $"'{symbol.Name}' is 'async void' but is not an event handler",
                "An exception thrown after the first await in an async void method is raised on the synchronization context (or crashes the process) and cannot be caught by the caller.",
                ValidationLevel.StaticProven, "high",
                new[] { context.Evidence("declaration", $"Method '{symbol.Name}' has the 'async' modifier and returns void; its signature is not (object, EventArgs).", method) },
                "Unobservable failures and no way for callers to await completion. Impact on a given runtime depends on the host's unhandled-exception policy.",
                "Return Task (or ValueTask) and await it at the call site. Keep async void only for true event handlers.",
                "Write a test whose body throws after an await and observe that the exception does not reach the caller.",
                new[] { "Callers that rely on fire-and-forget semantics deliberately are not distinguished from accidental ones." },
                discriminator: symbol.ToDisplayString());
        }
    }

    private static bool IsEventHandlerShape(IMethodSymbol method) =>
        method.Parameters.Length == 2 &&
        method.Parameters[0].Type.SpecialType == SpecialType.System_Object &&
        InheritsFrom(method.Parameters[1].Type, "EventArgs");

    private static bool InheritsFrom(ITypeSymbol type, string name)
    {
        for (var current = type; current is not null; current = current.BaseType)
            if (current.Name == name && current.ContainingNamespace?.ToDisplayString() == "System") return true;
        return false;
    }
}
