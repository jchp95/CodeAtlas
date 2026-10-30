using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

/// <summary>
/// Intraprocedural ownership check for a local holding an IDisposable: it leaks when it is neither disposed nor handed
/// elsewhere (returned, passed, stored, captured). Shared by ATL1006 and the call-graph rule for disposable factories.
/// </summary>
static class DisposalFlow
{
    private static readonly HashSet<string> DisposeMethods = new(StringComparer.Ordinal) { "Dispose", "DisposeAsync", "Close", "CloseAsync" };

    public static bool Leaks(SemanticModel model, SyntaxNode scope, ILocalSymbol local, CancellationToken cancellation)
    {
        foreach (var identifier in scope.DescendantNodes().OfType<IdentifierNameSyntax>())
        {
            if (identifier.Identifier.ValueText != local.Name) continue;
            if (!SymbolEqualityComparer.Default.Equals(model.GetSymbolInfo(identifier, cancellation).Symbol, local)) continue;

            switch (identifier.Parent)
            {
                case MemberAccessExpressionSyntax access when access.Expression == identifier:
                    if (DisposeMethods.Contains(access.Name.Identifier.ValueText)) return false;
                    continue; // x.Foo(): an ordinary use that keeps ownership local.
                case ConditionalAccessExpressionSyntax conditional when conditional.Expression == identifier:
                    if (conditional.WhenNotNull.DescendantNodesAndSelf().OfType<MemberBindingExpressionSyntax>()
                        .Any(binding => DisposeMethods.Contains(binding.Name.Identifier.ValueText))) return false;
                    continue;
                case ElementAccessExpressionSyntax:
                    continue;
                case EqualsValueClauseSyntax { Parent: VariableDeclaratorSyntax declarator } when declarator.Identifier.ValueText == local.Name:
                    continue;
                default:
                    // using (x), argument, return, assignment source, initializer, lambda capture, tuple…: disposed or ownership may transfer.
                    return false;
            }
        }
        return true;
    }
}
