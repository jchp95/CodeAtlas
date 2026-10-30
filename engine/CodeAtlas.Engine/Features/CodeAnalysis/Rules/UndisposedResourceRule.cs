using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

/// <summary>
/// A local IDisposable created with 'new' that is never disposed and never leaves the method.
/// Escape analysis is intraprocedural: passing, returning or storing the object counts as a transfer of ownership.
/// </summary>
sealed class UndisposedResourceRule : ICodeRule
{
    // Disposal of these is a no-op for correctness (no unmanaged state); flagging them is noise.
    private static readonly HashSet<string> ManagedOnly = new(StringComparer.Ordinal) { "MemoryStream", "StringReader", "StringWriter", "Task", "ValueTask" };

    public RuleDescriptor Descriptor { get; } = new("ATL1006", "UndisposedResource", "resources",
        FindingSeverity.Warning, "Local IDisposable is created, never disposed and does not escape the method.");

    public void Analyze(RuleContext context)
    {
        foreach (var declaration in context.Root.DescendantNodes().OfType<LocalDeclarationStatementSyntax>())
        {
            context.Cancellation.ThrowIfCancellationRequested();
            if (declaration.UsingKeyword.RawKind != 0) continue;
            var scope = declaration.EnclosingDeclaredMember() ?? declaration.Ancestors().OfType<CompilationUnitSyntax>().FirstOrDefault();
            if (scope is null) continue;

            foreach (var declarator in declaration.Declaration.Variables)
            {
                var creation = declarator.Initializer?.Value;
                if (creation is not (ObjectCreationExpressionSyntax or ImplicitObjectCreationExpressionSyntax)) continue;
                if (context.Model.GetTypeInfo(creation, context.Cancellation).Type is not { } type || type.TypeKind == TypeKind.Error) continue;
                if (!type.ImplementsDisposable() || ManagedOnly.Contains(type.Name)) continue;
                if (context.Model.GetDeclaredSymbol(declarator, context.Cancellation) is not ILocalSymbol local) continue;

                if (!DisposalFlow.Leaks(context.Model, scope, local, context.Cancellation)) continue;

                context.Report(declarator,
                    $"'{local.Name}' ({type.Name}) is created but never disposed",
                    $"The method creates a {type.ToDisplayString(SymbolDisplayFormat.CSharpShortErrorMessageFormat)}, uses it locally, and never calls Dispose/DisposeAsync or wraps it in 'using'.",
                    ValidationLevel.StaticRisk, "medium",
                    new[]
                    {
                        context.Evidence("creation", $"{type.Name} implements {(type.AllInterfaces.Any(i => i.Name == "IAsyncDisposable") ? "IAsyncDisposable/" : "")}IDisposable and is assigned to local '{local.Name}'.", declarator),
                        new FindingEvidence("no-disposal", "No Dispose/Close call, no 'using', and no escape (return, argument, field/property assignment, lambda capture) was found in the member.")
                    },
                    "Handles, sockets or buffers held by the object stay allocated until the finalizer runs (if there is one), which can exhaust handles or connections under load. Not measured.",
                    $"Declare it with 'using var {local.Name} = new {type.Name}(...)' (or 'await using' for IAsyncDisposable).",
                    "Run the member in a loop under dotnet-counters and watch handle/connection counts or GC finalization queue length; or add a test with a disposal-tracking double.",
                    new[] { "Ownership may be managed in a way the analysis cannot see (for example a callee disposing through an alias).", "Only the declaring member is analyzed." },
                    discriminator: declarator.ToString());
            }
        }
    }
}
