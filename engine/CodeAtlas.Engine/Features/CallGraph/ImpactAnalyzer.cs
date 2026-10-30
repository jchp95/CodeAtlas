sealed record NodeSummary(string Id, string Symbol, string Kind, string Project, GraphLocation? Location, string[] Tags);

sealed record ImpactItem(NodeSummary Node, string Certainty, int Depth, string Via, GraphLocation? CallSite, CallPathStep[]? Path);

sealed record ProjectImpact(string Project, string Certainty, int Consumers);

sealed record ImpactFlow(string Kind, string Label, NodeSummary EntryPoint, string Certainty, int Depth, CallPathStep[] Path);

sealed record ContractRisk(string Kind, string Certainty, string Description);

sealed record ImpactUnknowns(int UnresolvedCallSites, int DelegateInvocationsInConsumers, int OpenWorldDispatchSites, bool DepthLimited, bool ResultLimited, string[] NotModeled);

sealed record ImpactReport(
    int SchemaVersion,
    string Status,
    string Selector,
    NodeSummary? Symbol,
    NodeSummary[] Matches,
    ImpactItem[] DirectReferences,
    ImpactItem[] IndirectConsumers,
    NodeSummary[] ImplementationsAffected,
    NodeSummary[] ContractsImplemented,
    string[] NamespacesAffected,
    ProjectImpact[] ProjectsAffected,
    string[] LayersAffected,
    ImpactItem[] RelatedTests,
    ImpactFlow[] BusinessFlows,
    ContractRisk[] ContractRisks,
    ImpactUnknowns Unknowns,
    bool Exhaustive,
    string Statement,
    GraphCompleteness GraphCompleteness,
    GraphLimitNote[] LimitsApplied);

/// <summary>
/// "If I change this member, what should I review and test?" Proven consumers are reached through statically bound
/// calls only; potential consumers need a dispatch, delegate or lambda hand-off. Both are listed apart, and the result
/// says plainly when it cannot be exhaustive.
/// </summary>
static class ImpactAnalyzer
{
    public static ImpactReport Analyze(GraphIndex index, string selector, GraphLimits limits, ArchitectureConfig? architecture, string? project = null)
    {
        var selection = SymbolSelector.Resolve(index, selector, project);
        var completeness = index.Graph.Completeness;
        if (selection.Status != SelectorStatus.Found)
            return Empty(selection.Status == SelectorStatus.Ambiguous ? "ambiguous" : "notFound", selector, selection, completeness, index, selection.Note);

        var subject = selection.Matches[0];
        // A reference to the declared member is proven even when the callee is reached through dispatch; only fan-out, lambdas and delegates are potential.
        var proven = GraphTraversal.Reach(index, new[] { subject.Id }, forward: false, limits.MaxDepth, limits.MaxResults, definiteOnly: false, e => e.Certainty == EdgeCertainty.Proven);
        var any = GraphTraversal.Reach(index, new[] { subject.Id }, forward: false, limits.MaxDepth, limits.MaxResults, definiteOnly: false);

        var consumers = any.Visited.Values.Where(r => r.NodeId != subject.Id && index.Nodes.TryGetValue(r.NodeId, out var n) && !n.IsExternal).ToList();
        ImpactItem Item(Reached reached)
        {
            var isProven = proven.Visited.ContainsKey(reached.NodeId);
            var result = isProven ? proven : any;
            var pathReached = isProven ? proven.Visited[reached.NodeId] : reached;
            return new ImpactItem(Summary(index.Nodes[reached.NodeId]), isProven ? "proven" : "potential", pathReached.Depth,
                pathReached.Via is null ? "" : $"{pathReached.Via.Resolution}", pathReached.Via?.Site, ReversePath(index, result, reached.NodeId, subject.Id));
        }

        var items = consumers.OrderBy(r => proven.Visited.ContainsKey(r.NodeId) ? proven.Visited[r.NodeId].Depth : any.Visited[r.NodeId].Depth)
            .ThenBy(r => r.NodeId, StringComparer.Ordinal).Select(Item).ToList();
        var direct = index.InEdges(subject.Id).Where(e => index.Nodes.TryGetValue(e.From, out var n) && !n.IsExternal).GroupBy(e => e.From)
            .Select(g => (Edge: g.OrderBy(e => GraphIndex.IsDefinite(e) ? 0 : 1).First(), Node: index.Nodes[g.Key]))
            .OrderBy(t => t.Node.Id, StringComparer.Ordinal)
            .Select(t => new ImpactItem(Summary(t.Node), GraphIndex.IsDefinite(t.Edge) || t.Edge.Certainty == EdgeCertainty.Proven ? "proven" : "potential", 1,
                t.Edge.Resolution.ToString(), t.Edge.Site, null))
            .Take(limits.MaxResults).ToArray();

        var dispatch = index.Graph.Dispatch;
        var implementations = dispatch.Where(d => d.Declared == subject.Id).SelectMany(d => d.Targets).Distinct(StringComparer.Ordinal)
            .Where(index.Nodes.ContainsKey).Select(id => Summary(index.Nodes[id])).ToArray();
        var contracts = dispatch.Where(d => d.Targets.Contains(subject.Id)).Select(d => d.Declared).Where(index.Nodes.ContainsKey)
            .Select(id => Summary(index.Nodes[id])).ToArray();

        var consumerNodes = items.Select(i => index.Nodes[i.Node.Id]).ToList();
        var tests = items.Where(i => i.Node.Tags.Contains("test")).Take(limits.MaxResults).ToArray();
        var flows = FlowsReaching(index, any, proven, subject);
        var projects = consumerNodes.Append(subject).GroupBy(n => n.Project)
            .Select(g => new ProjectImpact(g.Key, g.Any(n => n.Id == subject.Id || proven.Visited.ContainsKey(n.Id)) ? "proven" : "potential", g.Count(n => n.Id != subject.Id)))
            .OrderBy(p => p.Project, StringComparer.Ordinal).ToArray();
        var layers = architecture is { Layers.Count: > 0 }
            ? consumerNodes.Append(subject).Select(architecture.LayerOfNode).Where(l => l is not null).Distinct().OrderBy(l => l, StringComparer.Ordinal).Cast<string>().ToArray()
            : Array.Empty<string>();
        var namespaces = consumerNodes.Append(subject).Select(n => n.Namespace).Where(n => !string.IsNullOrEmpty(n)).Distinct().OrderBy(n => n, StringComparer.Ordinal).Cast<string>().ToArray();

        var risks = ContractRisks(index, subject, implementations, consumerNodes, proven, flows);
        var delegatesInConsumers = consumerNodes.Count(n => index.OutEdges(n.Id).Any(e => e.Resolution == EdgeResolution.Delegate && e.To is null));
        var unknowns = new ImpactUnknowns(completeness.UnresolvedCallSites, delegatesInConsumers, completeness.OpenWorldDispatchSites,
            any.DepthLimited || proven.DepthLimited, any.ResultLimited || proven.ResultLimited,
            new[] { "Reflection, dynamic invocation and service-provider/DI resolution by string or convention", "Callers in code outside the analyzed directory", "Configuration-driven behavior (routes, handlers, serializers)" });
        var isPublic = risks.Any(r => r.Kind == "publicApi");
        var exhaustive = completeness.Closed && !unknowns.DepthLimited && !unknowns.ResultLimited && !isPublic;
        var statement = exhaustive
            ? "Within the analyzed projects every consumer is listed. Reflection, DI-by-convention and code outside the directory are not modeled."
            : "NOT exhaustive: " + string.Join("; ", new[]
            {
                completeness.UnresolvedCallSites > 0 ? $"{completeness.UnresolvedCallSites} call site(s) could not be bound" : null,
                delegatesInConsumers > 0 ? $"{delegatesInConsumers} consumer(s) invoke delegates whose targets are unknown" : null,
                isPublic ? "the member is externally visible, so unseen callers may exist" : null,
                unknowns.DepthLimited ? $"traversal stopped at depth {limits.MaxDepth}" : null,
                unknowns.ResultLimited ? $"results were capped at {limits.MaxResults}" : null,
                !completeness.Closed && completeness.OpenWorldDispatchSites > 0 ? "dispatch on externally visible members can reach code that was not analyzed" : null
            }.Where(x => x is not null)) + ". Treat the lists as known consumers, not as all consumers.";

        return new ImpactReport(1, "ok", selector, Summary(subject), Array.Empty<NodeSummary>(), direct, items.Where(i => i.Depth > 1 || !direct.Any(d => d.Node.Id == i.Node.Id)).Take(limits.MaxResults).ToArray(),
            implementations, contracts, namespaces, projects, layers, tests, flows, risks, unknowns, exhaustive, statement, completeness, index.Graph.LimitsApplied);
    }

    private static ImpactFlow[] FlowsReaching(GraphIndex index, ReachResult any, ReachResult proven, GraphNode subject)
    {
        var flows = new List<ImpactFlow>();
        foreach (var entry in index.Graph.EntryPoints)
        {
            if (entry.NodeId != subject.Id && !any.Visited.ContainsKey(entry.NodeId)) continue;
            var isProven = entry.NodeId == subject.Id || proven.Visited.ContainsKey(entry.NodeId);
            var result = isProven ? proven : any;
            var depth = entry.NodeId == subject.Id ? 0 : result.Visited[entry.NodeId].Depth;
            flows.Add(new ImpactFlow(entry.Kind, entry.Label, Summary(index.Nodes[entry.NodeId]), isProven ? "proven" : "potential", depth,
                entry.NodeId == subject.Id ? Array.Empty<CallPathStep>() : ReversePath(index, result, entry.NodeId, subject.Id)));
        }
        return flows.OrderBy(f => f.Label, StringComparer.Ordinal).ToArray();
    }

    private static ContractRisk[] ContractRisks(GraphIndex index, GraphNode subject, NodeSummary[] implementations, List<GraphNode> consumers, ReachResult proven, ImpactFlow[] flows)
    {
        var risks = new List<ContractRisk>();
        if (IsPublicSurface(subject) && !subject.Tags.Contains("test"))
            risks.Add(new ContractRisk("publicApi", "potential", "The member is externally visible. Changing its signature or behavior can break assemblies and services outside the analyzed directory, which this analysis cannot see."));
        if (implementations.Length > 0)
            risks.Add(new ContractRisk("implementations", "proven", $"{implementations.Length} known implementation(s)/override(s) must change together with this member: {string.Join(", ", implementations.Take(5).Select(i => i.Symbol))}{(implementations.Length > 5 ? ", …" : "")}."));
        var contractCount = index.Graph.Dispatch.Count(d => d.Targets.Contains(subject.Id));
        if (contractCount > 0)
            risks.Add(new ContractRisk("implementsContract", "proven", $"The member implements or overrides {contractCount} declared member(s); changing its signature breaks that contract, and changing its behavior affects every caller of the contract."));
        var consumersOfType = index.Graph.EntryPoints.Where(e => e.Kind == "message-consumer" && e.Contract is not null && subject.ContainingType is not null
            && (subject.ContainingType.Equals(e.Contract, StringComparison.Ordinal) || e.Contract.EndsWith("." + subject.ContainingType.Split('.').Last(), StringComparison.Ordinal))).ToArray();
        foreach (var consumer in consumersOfType)
            risks.Add(new ContractRisk("messageContract", "potential", $"The containing type is the message contract of consumer '{consumer.Label}'; serialized messages in flight or from other services may not match a changed shape."));
        if (subject.Tags.Contains("entry:http"))
            risks.Add(new ContractRisk("httpEndpoint", "proven", "The member is an HTTP entry point; changing its parameters or result changes the public HTTP contract."));
        if (flows.Length > 0)
            risks.Add(new ContractRisk("businessFlows", flows.Any(f => f.Certainty == "proven") ? "proven" : "potential", $"{flows.Length} entry point(s) reach this member: {string.Join(", ", flows.Take(5).Select(f => f.Label))}{(flows.Length > 5 ? ", …" : "")}."));
        return risks.ToArray();
    }

    private static bool IsPublicSurface(GraphNode node) => node.Accessibility is "public" or "protected" or "protectedorinternal" && !node.IsExternal
        && node.Kind is "method" or "constructor" or "accessor" or "operator";

    /// <summary>Path from <paramref name="from"/> (a consumer) down to the subject, following the BFS tree of the reverse traversal.</summary>
    private static CallPathStep[] ReversePath(GraphIndex index, ReachResult result, string from, string subject)
    {
        var steps = new List<CallPathStep>();
        var current = from;
        var guard = 0;
        while (result.Visited.TryGetValue(current, out var reached) && guard++ < 64)
        {
            var node = index.Nodes[current];
            steps.Add(index.Step(node, reached.Via is null ? "direct" : GraphIndex.ResolutionLabel(reached.Via), reached.Via?.Site));
            if (reached.ParentId is null) break;
            current = reached.ParentId;
        }
        return steps.ToArray();
    }

    private static NodeSummary Summary(GraphNode node) =>
        new(node.Id, $"{node.ContainingType}.{node.Signature}".TrimStart('.'), node.Kind, node.Project, node.Location, node.Tags);

    private static ImpactReport Empty(string status, string selector, SelectorResult selection, GraphCompleteness completeness, GraphIndex index, string? note) =>
        new(1, status, selector, null, selection.Matches.Select(Summary).ToArray(), Array.Empty<ImpactItem>(), Array.Empty<ImpactItem>(), Array.Empty<NodeSummary>(),
            Array.Empty<NodeSummary>(), Array.Empty<string>(), Array.Empty<ProjectImpact>(), Array.Empty<string>(), Array.Empty<ImpactItem>(), Array.Empty<ImpactFlow>(),
            Array.Empty<ContractRisk>(), new ImpactUnknowns(0, 0, 0, false, false, Array.Empty<string>()), false, note ?? "No symbol was selected.", completeness, index.Graph.LimitsApplied);
}
