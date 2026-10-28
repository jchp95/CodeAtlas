using Microsoft.CodeAnalysis;

internal sealed record DiscoveredEntryPoint(string Label, string Kind, string File, int Line, SyntaxNode? Handler, string? Contract);

internal static partial class BusinessFlowAnalyzer
{
    /// <summary>
    /// Entry-point discovery (Minimal API maps, controller actions, MassTransit consumers) exposed to the shared
    /// call graph so both engines start from the same set of entries instead of two diverging detectors.
    /// </summary>
    internal static IReadOnlyList<DiscoveredEntryPoint> DiscoverSharedEntryPoints(
        string repositoryRoot,
        SyntaxTree[] syntaxTrees,
        IReadOnlyDictionary<SyntaxTree, SemanticModel> semanticModels,
        IReadOnlyDictionary<string, string[]> repositoryTypeIndex) =>
        DiscoverEntryPoints(repositoryRoot, syntaxTrees, semanticModels, repositoryTypeIndex)
            .Select(entry => new DiscoveredEntryPoint(entry.Endpoint, entry.Kind, entry.File, entry.Line, entry.HandlerNode, entry.Contract))
            .ToArray();
}
