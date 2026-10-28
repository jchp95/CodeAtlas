/// <summary>Merges project fragments and resolves dispatch against the whole solution (class-hierarchy analysis).</summary>
sealed class CallGraphAssembler
{
    private readonly GraphLimits limits;
    public CallGraphAssembler(GraphLimits limits) => this.limits = limits;

    public sealed record Result(GraphNode[] Nodes, GraphEdge[] Edges, GraphEntryPoint[] EntryPoints, GraphLimitNote[] LimitNotes, string[] Warnings, bool HitHardLimit)
    {
        public GraphCompleteness Completeness(bool cancelled)
        {
            var unresolved = Edges.Count(e => e.Resolution == EdgeResolution.Unresolved);
            var delegates = Edges.Count(e => e.Resolution == EdgeResolution.Delegate && e.To is null);
            var openWorld = Edges.Where(e => e.OpenWorld).Select(e => e.SiteId).Distinct(StringComparer.Ordinal).Count();
            var truncated = Edges.Where(e => e.Truncated).Select(e => e.SiteId).Distinct(StringComparer.Ordinal).Count();
            var closed = !cancelled && unresolved + delegates + openWorld + truncated == 0 && !HitHardLimit;
            var statement = closed
                ? "Every call site in the analyzed projects resolves to a known target and no dispatch can reach code outside the solution."
                : "The graph is NOT complete: it lists the targets that could be proven or enumerated, but unresolved call sites, delegate invocations, dispatch on members visible outside the solution, truncated fan-out or an interrupted build mean other targets may exist.";
            return new GraphCompleteness(closed, unresolved, delegates, openWorld, truncated, statement);
        }
    }

    public Result Assemble(IReadOnlyList<ProjectFragment> fragments)
    {
        var nodes = new Dictionary<string, GraphNode>(StringComparer.Ordinal);
        foreach (var fragment in fragments)
            foreach (var node in fragment.Nodes)
                nodes[node.Id] = nodes.TryGetValue(node.Id, out var existing) ? Merge(existing, node) : node;

        var implementations = fragments.SelectMany(f => f.Implementations).GroupBy(p => p.Declared, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.DistinctBy(p => p.Implementation).ToArray(), StringComparer.Ordinal);
        var overrides = fragments.SelectMany(f => f.Overrides).GroupBy(p => p.Declared, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.DistinctBy(p => p.Implementation).ToArray(), StringComparer.Ordinal);

        var edges = new List<GraphEdge>();
        var notes = new List<GraphLimitNote>();
        var warnings = new List<string>();
        var truncatedSites = 0;
        var hitHardLimit = false;
        var stubs = new HashSet<string>(StringComparer.Ordinal);

        string NodeFor(string symbolId)
        {
            var id = GraphIdentity.NodeId(symbolId);
            if (!nodes.ContainsKey(id) && stubs.Add(id))
            {
                var project = symbolId.Contains("::", StringComparison.Ordinal) ? symbolId[..symbolId.IndexOf("::", StringComparison.Ordinal)] : "(unknown)";
                nodes[id] = new GraphNode(id, symbolId, "method", project, symbolId, symbolId, null, null, "unknown", symbolId.StartsWith("ext:", StringComparison.Ordinal),
                    false, false, new[] { "stub" }, null);
            }
            return id;
        }

        foreach (var raw in fragments.SelectMany(f => f.Edges))
        {
            if (edges.Count >= limits.MaxEdges)
            {
                hitHardLimit = true;
                notes.Add(new GraphLimitNote("maxEdges", limits.MaxEdges, "Edge collection stopped; the graph is truncated."));
                break;
            }

            if (raw.Target is null)
            {
                edges.Add(Edge(raw, null, raw.Resolution, raw.Certainty, false));
                continue;
            }

            var declaredId = NodeFor(raw.Target);
            var isDispatch = raw.Resolution is EdgeResolution.InterfaceDispatch or EdgeResolution.VirtualDispatch;
            if (!isDispatch)
            {
                edges.Add(Edge(raw, declaredId, raw.Resolution, raw.Certainty, false));
                continue;
            }

            var targets = KnownTargets(raw, implementations, overrides);
            var truncated = targets.Count > limits.MaxDispatchTargets;
            if (truncated)
            {
                truncatedSites++;
                targets = targets.Take(limits.MaxDispatchTargets).ToList();
            }
            var resolution = raw.Resolution;
            // The reference to the declared member is certain; which implementation runs is not.
            edges.Add(Edge(raw, declaredId, resolution, EdgeCertainty.Proven, truncated));
            foreach (var target in targets.Where(t => !string.Equals(t, raw.Target, StringComparison.Ordinal)))
                edges.Add(Edge(raw, NodeFor(target), resolution, EdgeCertainty.Potential, truncated));
        }

        if (truncatedSites > 0)
            notes.Add(new GraphLimitNote("maxDispatchTargets", limits.MaxDispatchTargets,
                $"{truncatedSites} dispatch call site(s) had more known targets than the limit; the extra targets were omitted and the sites are marked truncated."));
        if (nodes.Count > limits.MaxNodes)
        {
            hitHardLimit = true;
            notes.Add(new GraphLimitNote("maxNodes", limits.MaxNodes, $"The solution has {nodes.Count} nodes; the limit was exceeded and consumers should treat the graph as partial."));
        }
        if (stubs.Count > 0) warnings.Add($"{stubs.Count} call target(s) had no declaring node (project not analyzed or interrupted); they appear as stub nodes.");

        var entryPoints = fragments.SelectMany(f => f.EntryPoints).Where(e => nodes.ContainsKey(e.NodeId))
            .DistinctBy(e => (e.NodeId, e.Label)).OrderBy(e => e.Label, StringComparer.Ordinal).ThenBy(e => e.NodeId, StringComparer.Ordinal).ToArray();

        return new Result(nodes.Values.OrderBy(n => n.Id, StringComparer.Ordinal).ToArray(),
            edges.OrderBy(e => e.From, StringComparer.Ordinal).ThenBy(e => e.SiteId, StringComparer.Ordinal).ThenBy(e => e.To, StringComparer.Ordinal).ToArray(),
            entryPoints, notes.ToArray(), warnings.ToArray(), hitHardLimit);
    }

    /// <summary>All implementations/overrides known for the declared member, restricted to types compatible with the receiver's static type.</summary>
    private static List<string> KnownTargets(RawEdge raw, Dictionary<string, DispatchPair[]> implementations, Dictionary<string, DispatchPair[]> overrides)
    {
        var result = new SortedSet<string>(StringComparer.Ordinal);
        bool Compatible(DispatchPair pair) => raw.ReceiverTypeId is null || pair.ImplementerTypes.Contains(raw.ReceiverTypeId, StringComparer.Ordinal);

        if (implementations.TryGetValue(raw.Target!, out var implementers))
            foreach (var pair in implementers.Where(Compatible))
            {
                result.Add(pair.Implementation);
                // A derived class may override the implementation that satisfies the interface.
                if (overrides.TryGetValue(pair.Implementation, out var further))
                    foreach (var overriding in further.Where(Compatible)) result.Add(overriding.Implementation);
            }
        if (overrides.TryGetValue(raw.Target!, out var derived))
            foreach (var pair in derived.Where(Compatible)) result.Add(pair.Implementation);
        return result.ToList();
    }

    private static GraphEdge Edge(RawEdge raw, string? to, EdgeResolution resolution, EdgeCertainty certainty, bool truncated) =>
        new("E-" + GraphIdentity.StableHash($"{raw.From}|{to}|{raw.SiteId}|{resolution}"), raw.From, to, resolution, certainty, raw.SiteId, raw.Site,
            raw.InLoop, raw.InTry, raw.PassesToken, raw.TargetAcceptsToken, raw.OpenWorld, truncated, raw.Label, raw.Reason);

    private static GraphNode Merge(GraphNode a, GraphNode b)
    {
        var preferred = a.IsExternal && !b.IsExternal ? b : a;
        return preferred with { Tags = a.Tags.Concat(b.Tags).Distinct(StringComparer.Ordinal).OrderBy(t => t, StringComparer.Ordinal).ToArray() };
    }
}
