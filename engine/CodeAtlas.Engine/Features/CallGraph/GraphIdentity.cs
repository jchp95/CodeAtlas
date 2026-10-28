using System.Security.Cryptography;
using System.Text;
using Microsoft.CodeAnalysis;

/// <summary>
/// Symbol-based identity. A node id is a hash of the declaring project and the symbol's documentation-comment id
/// (namespace + type + member + parameter types + generic arity), so it never involves a file or line.
/// Preserved: moving code between files/lines, reformatting, changing bodies, splitting a partial type, editing
/// parameter names, defaults and return types of non-conversion methods.
/// New identity: renaming, changing the namespace/containing type, changing parameter types or generic arity,
/// moving the symbol to another project. Lambdas are identified by owner + order of appearance in the owner,
/// so inserting a lambda before another one in the same method renumbers the later ones.
/// </summary>
sealed class GraphIdentity
{
    private readonly IReadOnlyDictionary<string, string> projectByAssembly;

    public GraphIdentity(IReadOnlyDictionary<string, string> projectByAssembly) => this.projectByAssembly = projectByAssembly;

    public string ProjectOf(ISymbol symbol) =>
        symbol.ContainingAssembly is { } assembly && projectByAssembly.TryGetValue(assembly.Name, out var project) ? project : "(external)";

    public bool IsSource(ISymbol symbol) => symbol.ContainingAssembly is { } assembly && projectByAssembly.ContainsKey(assembly.Name);

    public string SymbolId(ISymbol symbol)
    {
        symbol = Canonical(symbol);
        var docId = symbol.GetDocumentationCommentId();
        if (docId is null) docId = symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        return IsSource(symbol) ? $"{ProjectOf(symbol)}::{docId}" : $"ext:{symbol.ContainingAssembly?.Name}::{docId}";
    }

    public static string NodeId(string symbolId) =>
        "N-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(symbolId))).ToLowerInvariant()[..14];

    public string NodeIdOf(ISymbol symbol) => NodeId(SymbolId(symbol));

    public string TypeId(ITypeSymbol type) =>
        (type.OriginalDefinition.GetDocumentationCommentId() ?? type.OriginalDefinition.ToDisplayString());

    /// <summary>Generic instantiations collapse to their definition; a partial method collapses to its implementation part.</summary>
    public static ISymbol Canonical(ISymbol symbol)
    {
        symbol = symbol.OriginalDefinition;
        if (symbol is IMethodSymbol { PartialImplementationPart: { } implementation }) return implementation.OriginalDefinition;
        return symbol;
    }

    public static string StableHash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant()[..14];
}
