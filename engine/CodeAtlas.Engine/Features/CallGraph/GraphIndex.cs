/// <summary>Adjacency and lookup structures over an immutable <see cref="CallGraph"/>.</summary>
sealed class GraphIndex
{
    public CallGraph Graph { get; }
    public IReadOnlyDictionary<string, GraphNode> Nodes { get; }
    public IReadOnlyDictionary<string, List<GraphEdge>> Out { get; }
    public IReadOnlyDictionary<string, List<GraphEdge>> In { get; }

    public GraphIndex(CallGraph graph)
    {
        Graph = graph;
        Nodes = graph.Nodes.ToDictionary(n => n.Id, StringComparer.Ordinal);
        var outgoing = new Dictionary<string, List<GraphEdge>>(StringComparer.Ordinal);
        var incoming = new Dictionary<string, List<GraphEdge>>(StringComparer.Ordinal);
        foreach (var edge in graph.Edges)
        {
            if (!outgoing.TryGetValue(edge.From, out var o)) outgoing[edge.From] = o = new List<GraphEdge>();
            o.Add(edge);
            if (edge.To is null) continue;
            if (!incoming.TryGetValue(edge.To, out var i)) incoming[edge.To] = i = new List<GraphEdge>();
            i.Add(edge);
        }
        Out = outgoing;
        In = incoming;
    }

    /// <summary>A call whose callee is certain: statically bound and not subject to dispatch, delegates or lambda hand-off.</summary>
    public static bool IsDefinite(GraphEdge edge) =>
        edge.To is not null && edge.Certainty == EdgeCertainty.Proven
        && edge.Resolution is EdgeResolution.Direct or EdgeResolution.ExtensionMethod or EdgeResolution.ObjectCreation or EdgeResolution.PropertyAccess;

    public IReadOnlyList<GraphEdge> OutEdges(string id) => Out.TryGetValue(id, out var list) ? list : Array.Empty<GraphEdge>();
    public IReadOnlyList<GraphEdge> InEdges(string id) => In.TryGetValue(id, out var list) ? list : Array.Empty<GraphEdge>();

    public string TypeOf(GraphNode node) => node.ContainingType ?? node.Project;

    public CallPathStep Step(GraphNode node, string resolution, GraphLocation? site = null) =>
        new($"{node.ContainingType}.{node.Signature}".TrimStart('.'),
            site is null && node.Location is null ? null : new FindingLocation((site ?? node.Location!).File, (site ?? node.Location!).Line, 1, (site ?? node.Location!).Line, node.Signature, node.Project),
            resolution);

    public static string ResolutionLabel(GraphEdge edge) => edge.Resolution switch
    {
        EdgeResolution.Direct or EdgeResolution.ExtensionMethod or EdgeResolution.ObjectCreation or EdgeResolution.PropertyAccess => "direct",
        EdgeResolution.InterfaceDispatch => "interface",
        EdgeResolution.VirtualDispatch => "virtual",
        EdgeResolution.Delegate or EdgeResolution.Lambda => "delegate",
        _ => "unknown"
    };
}

enum SelectorStatus { Found, Ambiguous, NotFound }

sealed record SelectorResult(SelectorStatus Status, GraphNode[] Matches, string Selector, string? Note);

static class SymbolSelector
{
    /// <summary>
    /// Selector forms: a stable node id (<c>N-…</c>); a position (<c>path/File.cs:LINE</c>, optionally <c>:COLUMN</c>, resolved to the innermost
    /// member that spans it); or a name (<c>Type.Method</c>, <c>Namespace.Type.Method(int)</c>, or part of the symbol id).
    /// </summary>
    public static SelectorResult Resolve(GraphIndex index, string selector, string? project = null)
    {
        selector = selector.Trim();
        IEnumerable<GraphNode> candidates = index.Graph.Nodes.Where(n => !n.IsExternal && n.Kind != "initializer" && (project is null || n.Project == project));

        if (index.Nodes.TryGetValue(selector, out var byId)) return new SelectorResult(SelectorStatus.Found, new[] { byId }, selector, null);

        var position = System.Text.RegularExpressions.Regex.Match(selector, @"^(?<file>.+?):(?<line>\d+)(?::(?<col>\d+))?$");
        if (position.Success)
        {
            var file = position.Groups["file"].Value.Replace('\\', '/');
            var line = int.Parse(position.Groups["line"].Value);
            var inFile = candidates.Where(n => n.Location is not null && n.EndLine > 0
                && (n.Location.File.Equals(file, StringComparison.OrdinalIgnoreCase) || n.Location.File.EndsWith("/" + file, StringComparison.OrdinalIgnoreCase)
                    || file.EndsWith("/" + n.Location.File, StringComparison.OrdinalIgnoreCase))
                && SpanStart(n) <= line && line <= n.EndLine).ToArray();
            if (inFile.Length == 0) return new SelectorResult(SelectorStatus.NotFound, Array.Empty<GraphNode>(), selector, $"No analyzed member spans {file}:{line}.");
            // Innermost: the candidate that starts last (a lambda inside a method), ties are genuinely ambiguous.
            var innermostStart = inFile.Max(SpanStart);
            var innermost = inFile.Where(n => SpanStart(n) == innermostStart).ToArray();
            return new SelectorResult(innermost.Length == 1 ? SelectorStatus.Found : SelectorStatus.Ambiguous, innermost, selector, null);
        }

        var exact = candidates.Where(n => string.Equals(n.SymbolId, selector, StringComparison.Ordinal)
            || string.Equals(n.SymbolId[(n.SymbolId.IndexOf("::", StringComparison.Ordinal) + 2)..], selector, StringComparison.Ordinal)).ToArray();
        if (exact.Length > 0) return new SelectorResult(exact.Length == 1 ? SelectorStatus.Found : SelectorStatus.Ambiguous, exact, selector, null);

        var normalized = selector.Replace(" ", "");
        var named = candidates.Where(n => Matches(n, normalized)).OrderBy(n => n.SymbolId, StringComparer.Ordinal).ToArray();
        return named.Length switch
        {
            0 => new SelectorResult(SelectorStatus.NotFound, Array.Empty<GraphNode>(), selector, "No analyzed member matches this name."),
            1 => new SelectorResult(SelectorStatus.Found, named, selector, null),
            _ => new SelectorResult(SelectorStatus.Ambiguous, named.Take(50).ToArray(), selector, $"{named.Length} members match; pick one by id.")
        };
    }

    private static int SpanStart(GraphNode n) => n.Location!.Line;

    private static bool Matches(GraphNode node, string selector)
    {
        var qualified = $"{node.ContainingType}.{node.Name}".Replace(" ", "");
        var withSignature = $"{node.ContainingType}.{node.Signature}".Replace(" ", "");
        return qualified.Equals(selector, StringComparison.Ordinal) || qualified.EndsWith("." + selector, StringComparison.Ordinal)
            || withSignature.Contains(selector, StringComparison.Ordinal) && selector.Contains('(');
    }
}
