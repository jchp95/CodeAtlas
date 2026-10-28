using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

static class RoslynHelpers
{
    public static bool IsNamed(this ITypeSymbol? type, string ns, string name) =>
        type is not null && type.Name == name && type.ContainingNamespace?.ToDisplayString() == ns;

    public static bool IsTaskLike(this ITypeSymbol? type) =>
        type is not null && type.ContainingNamespace?.ToDisplayString() == "System.Threading.Tasks" && type.Name is "Task" or "ValueTask";

    public static bool IsCancellationToken(this ITypeSymbol? type) => type.IsNamed("System.Threading", "CancellationToken");

    public static bool ImplementsDisposable(this ITypeSymbol type) =>
        type.AllInterfaces.Any(i => i.IsNamed("System", "IDisposable") || i.IsNamed("System", "IAsyncDisposable"));

    public static IOperation Unwrap(this IOperation operation)
    {
        while (operation is IConversionOperation { IsImplicit: true } conversion) operation = conversion.Operand;
        return operation;
    }

    /// <summary>The nearest enclosing declared member (method, constructor, accessor, local function), if any.</summary>
    public static SyntaxNode? EnclosingMember(this SyntaxNode node) =>
        node.Ancestors().FirstOrDefault(a => a is BaseMethodDeclarationSyntax or AccessorDeclarationSyntax or LocalFunctionStatementSyntax
            or AnonymousFunctionExpressionSyntax);

    public static SyntaxNode? EnclosingDeclaredMember(this SyntaxNode node) =>
        node.Ancestors().FirstOrDefault(a => a is BaseMethodDeclarationSyntax or AccessorDeclarationSyntax or LocalFunctionStatementSyntax);

    public static bool IsInsideLambdaOrLocalFunction(this SyntaxNode node, SyntaxNode boundary) =>
        node.Ancestors().TakeWhile(a => a != boundary).Any(a => a is AnonymousFunctionExpressionSyntax or LocalFunctionStatementSyntax);
}
