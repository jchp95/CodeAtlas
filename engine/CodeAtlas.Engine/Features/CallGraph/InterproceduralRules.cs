sealed record InterproceduralReport(int SchemaVersion, GraphCompleteness GraphCompleteness, Finding[] Findings, RuleDescriptor[] Rules, string[] Notes, GraphLimitNote[] LimitsApplied);

/// <summary>
/// Cross-method rules evaluated on the call graph. Each one is a bounded reverse search from a small set of "seed" nodes
/// (computed once), never a per-diagnostic walk of the whole graph. Chains follow statically bound calls only, so a path
/// reported here exists in the code; whether it executes, and how often, is not known.
/// </summary>
static class InterproceduralRules
{
    public static InterproceduralReport Run(GraphIndex index, GraphLimits limits, CancellationToken cancellation)
    {
        var notes = new List<string>();
        var findings = new List<Finding>();
        findings.AddRange(BlockingReachedFromAsync(index, limits, notes, cancellation));
        findings.AddRange(RemoteCallInLoop(index, limits, notes, cancellation));
        findings.AddRange(CancellationDropped(index, limits, notes, cancellation));
        findings.AddRange(UndisposedFactoryResult(index, limits));
        findings.AddRange(NullFlows(index));

        var capped = findings.GroupBy(f => f.RuleId).SelectMany(g =>
        {
            if (g.Count() > limits.MaxResults) notes.Add($"{g.Key}: {g.Count()} findings; only the first {limits.MaxResults} are listed.");
            return g.Take(limits.MaxResults);
        }).OrderByDescending(f => f.Severity).ThenBy(f => f.RuleId, StringComparer.Ordinal).ThenBy(f => f.Location.File, StringComparer.Ordinal).ThenBy(f => f.Location.Line).ToArray();
        notes.Add($"Chains are searched to depth {limits.MaxDepth} over statically bound calls; longer chains and chains that need a dispatch, delegate or lambda hand-off are not reported.");
        if (!index.Graph.Completeness.Closed) notes.Add("The call graph is not complete (see graphCompleteness); absence of a finding is not evidence of absence.");

        return new InterproceduralReport(1, index.Graph.Completeness, capped, GraphFindings.Descriptors.Where(d => d.Id.StartsWith("ATL31", StringComparison.Ordinal)).ToArray(),
            notes.Distinct(StringComparer.Ordinal).ToArray(), index.Graph.LimitsApplied);
    }

    private static bool Has(GraphNode node, string tag) => node.Tags.Contains(tag);

    private static bool Source(GraphIndex index, string id) => index.Nodes.TryGetValue(id, out var n) && !n.IsExternal;

    private static IEnumerable<Finding> BlockingReachedFromAsync(GraphIndex index, GraphLimits limits, List<string> notes, CancellationToken cancellation)
    {
        var seeds = index.Graph.Edges.Where(e => GraphIndex.IsDefinite(e) && Source(index, e.From) && index.Nodes[e.To!].IsExternal && Has(index.Nodes[e.To!], "blocking"))
            .Select(e => e.From).Distinct(StringComparer.Ordinal).ToArray();
        if (seeds.Length == 0) yield break;
        var reach = GraphTraversal.Reach(index, seeds, forward: false, limits.MaxDepth, int.MaxValue / 2, definiteOnly: true);
        if (reach.DepthLimited) notes.Add("ATL3101: some chains were cut at the depth limit.");

        foreach (var start in reach.Visited.Values.Where(r => r.Depth >= 1).OrderBy(r => r.NodeId, StringComparer.Ordinal))
        {
            cancellation.ThrowIfCancellationRequested();
            var node = index.Nodes[start.NodeId];
            if (node.IsExternal || !Has(node, "async") || Has(node, "test") || seeds.Contains(node.Id)) continue;
            var firstHop = index.Nodes[start.ParentId!];
            // A call to another async method is that method's finding; this rule is about asynchronous code calling a synchronous helper that blocks.
            if (Has(firstHop, "async")) continue;

            var chain = ChainToSeed(index, reach, start.NodeId);
            var seed = index.Nodes[chain[^1].Id];
            var blockEdge = index.OutEdges(seed.Id).First(e => e.To is not null && index.Nodes[e.To].IsExternal && Has(index.Nodes[e.To], "blocking"));
            var steps = chain.Select(c => index.Step(index.Nodes[c.Id], c.Resolution)).Append(new CallPathStep(index.Nodes[blockEdge.To!].Signature, new FindingLocation(blockEdge.Site.File, blockEdge.Site.Line, 1, blockEdge.Site.Line, null, seed.Project), "direct")).ToArray();
            yield return GraphFindings.Create("ATL3101", FindingSeverity.Warning, ValidationLevel.StaticProven,
                $"{node.Signature} (async) reaches a blocking wait through {firstHop.Signature}",
                $"The asynchronous member {node.ContainingType}.{node.Signature} calls the synchronous {firstHop.ContainingType}.{firstHop.Signature}, which leads to {index.Nodes[blockEdge.To!].ContainingType}.{index.Nodes[blockEdge.To!].Name} after {chain.Count - 1} call(s).",
                GraphFindings.LocationOf(node, start.Via!.Site),
                chain.Select(c => new FindingEvidence("call-chain", $"{index.Nodes[c.Id].ContainingType}.{index.Nodes[c.Id].Signature}", c.Edge is null ? null : new FindingLocation(c.Edge.Site.File, c.Edge.Site.Line, 1, c.Edge.Site.Line, null, index.Nodes[c.Id].Project)))
                    .Append(new FindingEvidence("blocking-call", $"{index.Nodes[blockEdge.To!].ContainingType}.{index.Nodes[blockEdge.To!].Name} blocks the thread", new FindingLocation(blockEdge.Site.File, blockEdge.Site.Line, 1, blockEdge.Site.Line, null, seed.Project))).ToArray(),
                steps, "medium",
                new[] { "The chain exists in the code; deadlock or thread-pool starvation depends on the synchronization context and load, which are not observed.", "Only statically bound calls are followed." },
                "The async caller holds a thread for the whole wait; under load this contributes to thread-pool starvation, and with a synchronization context it can deadlock. Not measured.",
                $"Make {firstHop.Signature} (and the helpers on the path) asynchronous and await it from {node.Signature}.",
                "Run the async member under load while sampling dotnet-counters (ThreadPool Queue Length, Thread Count), or in a single-threaded SynchronizationContext test.",
                $"{node.Id}|{firstHop.Id}");
        }
    }

    private static IEnumerable<Finding> RemoteCallInLoop(GraphIndex index, GraphLimits limits, List<string> notes, CancellationToken cancellation)
    {
        var remote = index.Graph.Edges.Where(e => GraphIndex.IsDefinite(e) && Source(index, e.From) && index.Nodes[e.To!].IsExternal && index.Nodes[e.To!].Tags.Any(t => t.StartsWith("remote:", StringComparison.Ordinal)))
            .GroupBy(e => e.From).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        if (remote.Count == 0) yield break;
        var reach = GraphTraversal.Reach(index, remote.Keys, forward: false, limits.MaxDepth, int.MaxValue / 2, definiteOnly: true);

        foreach (var edge in index.Graph.Edges.Where(e => e.InLoop && GraphIndex.IsDefinite(e) && e.To is not null && e.To != e.From && reach.Visited.ContainsKey(e.To) && Source(index, e.To)))
        {
            cancellation.ThrowIfCancellationRequested();
            var caller = index.Nodes[edge.From];
            if (!Source(index, edge.From) || Has(caller, "test")) continue;
            var callee = index.Nodes[edge.To!];
            var chain = ChainToSeed(index, reach, callee.Id);
            var seed = index.Nodes[chain[^1].Id];
            var op = remote[seed.Id];
            var opNode = index.Nodes[op.To!];
            var kind = opNode.Tags.First(t => t.StartsWith("remote:", StringComparison.Ordinal))[7..];
            var steps = new[] { index.Step(caller, "direct", edge.Site) }.Concat(chain.Select(c => index.Step(index.Nodes[c.Id], c.Resolution)))
                .Append(new CallPathStep(opNode.Signature, new FindingLocation(op.Site.File, op.Site.Line, 1, op.Site.Line, null, seed.Project), "direct")).ToArray();
            yield return GraphFindings.Create("ATL3102", FindingSeverity.Warning, ValidationLevel.StaticRisk,
                $"Loop in {caller.Signature} calls {callee.Signature}, which performs a remote ({kind}) operation",
                $"The call at {edge.Site.File}:{edge.Site.Line} runs once per loop iteration and reaches {opNode.ContainingType}.{opNode.Name} after {chain.Count} call(s).",
                GraphFindings.LocationOf(caller, edge.Site),
                new[] { new FindingEvidence("loop-call", $"{caller.ContainingType}.{caller.Signature} calls {callee.Signature} inside a loop", new FindingLocation(edge.Site.File, edge.Site.Line, 1, edge.Site.Line, null, caller.Project)) }
                    .Concat(chain.Select(c => new FindingEvidence("call-chain", $"{index.Nodes[c.Id].ContainingType}.{index.Nodes[c.Id].Signature}"))).ToArray(),
                steps, "medium",
                new[] { "Loop bounds and the cost of the remote operation are unknown.", "A deliberately sequential or throttled loop is indistinguishable from an accidental N+1.", $"Only chains of up to {limits.MaxDepth} statically bound calls are followed." },
                $"If the loop is long, latency and load grow with the iteration count because every iteration performs a {kind} round trip. Not measured.",
                "Fetch or send the data in one batched operation before the loop, or restructure so the loop works on in-memory data.",
                "Count the remote operations per request with a trace (HTTP client or database command events) for a realistic input size.",
                $"{caller.Id}|{edge.SiteId}");
        }
    }

    private static IEnumerable<Finding> CancellationDropped(GraphIndex index, GraphLimits limits, List<string> notes, CancellationToken cancellation)
    {
        var sinks = index.Graph.Edges.Where(e => e.To is not null && e.TargetAcceptsToken && !e.PassesToken && Source(index, e.From) && !Has(index.Nodes[e.From], "token-param") && !Has(index.Nodes[e.From], "test")
                && (e.Resolution is EdgeResolution.Direct or EdgeResolution.ExtensionMethod) && index.Nodes[e.To].IsExternal)
            .GroupBy(e => e.From).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        if (sinks.Count == 0) yield break;
        var reach = GraphTraversal.Reach(index, sinks.Keys, forward: false, limits.MaxDepth, int.MaxValue / 2, definiteOnly: true,
            edge => !Has(index.Nodes[edge.To!], "token-param"));

        foreach (var start in reach.Visited.Values.Where(r => r.Depth >= 1).OrderBy(r => r.NodeId, StringComparer.Ordinal))
        {
            cancellation.ThrowIfCancellationRequested();
            var caller = index.Nodes[start.NodeId];
            if (caller.IsExternal || !Has(caller, "token-param") || Has(caller, "test")) continue;
            var chain = ChainToSeed(index, reach, caller.Id);
            var sinkNode = index.Nodes[chain[^1].Id];
            var sink = sinks[sinkNode.Id];
            var target = index.Nodes[sink.To!];
            var steps = chain.Select(c => index.Step(index.Nodes[c.Id], c.Resolution)).Append(new CallPathStep(target.Signature, new FindingLocation(sink.Site.File, sink.Site.Line, 1, sink.Site.Line, null, sinkNode.Project), "direct")).ToArray();
            var firstHop = index.Nodes[start.ParentId!];
            yield return GraphFindings.Create("ATL3103", FindingSeverity.Warning, ValidationLevel.StaticProven,
                $"Cancellation from {caller.Signature} is lost before {target.ContainingType}.{target.Name}",
                $"{caller.Signature} receives a CancellationToken but calls {firstHop.Signature}, which has no token parameter; {sinkNode.Signature} then calls {target.ContainingType}.{target.Name}, which accepts one.",
                GraphFindings.LocationOf(caller, start.Via!.Site),
                chain.Select(c => new FindingEvidence("call-chain", $"{index.Nodes[c.Id].ContainingType}.{index.Nodes[c.Id].Signature}")).Append(new FindingEvidence("ignored-token", $"{target.ContainingType}.{target.Name} could take a CancellationToken but is called without one", new FindingLocation(sink.Site.File, sink.Site.Line, 1, sink.Site.Line, null, sinkNode.Project))).ToArray(),
                steps, "medium",
                new[] { "Whether cancellation matters for this operation is a design decision.", $"Only statically bound call chains up to depth {limits.MaxDepth} are followed." },
                "A request cancelled by the client keeps running this operation to completion.",
                $"Add a CancellationToken parameter to {firstHop.Signature} (and intermediate methods) and pass it down to {target.Name}.",
                "Cancel the token while the operation runs and assert it ends with OperationCanceledException.",
                $"{caller.Id}|{firstHop.Id}");
        }
    }

    private static IEnumerable<Finding> UndisposedFactoryResult(GraphIndex index, GraphLimits limits)
    {
        foreach (var edge in index.Graph.Edges.Where(e => e.ResultNotDisposed && e.To is not null && Source(index, e.From)))
        {
            var factory = index.Nodes[edge.To!];
            if (factory.IsExternal || !Has(factory, "disposable-creator")) continue;
            var caller = index.Nodes[edge.From];
            yield return GraphFindings.Create("ATL3104", FindingSeverity.Warning, ValidationLevel.StaticRisk,
                $"{caller.Signature} never disposes the object returned by {factory.Signature}",
                $"{factory.ContainingType}.{factory.Signature} returns a newly created IDisposable, so the caller owns it; {caller.Signature} drops it without disposing or passing it on.",
                GraphFindings.LocationOf(caller, edge.Site),
                new[]
                {
                    new FindingEvidence("factory", $"{factory.ContainingType}.{factory.Signature} returns 'new' disposable object", factory.Location is null ? null : new FindingLocation(factory.Location.File, factory.Location.Line, 1, factory.Location.Line, factory.Signature, factory.Project)),
                    new FindingEvidence("discarded-result", "The result is discarded or kept in a local that is never disposed and never leaves the member.", new FindingLocation(edge.Site.File, edge.Site.Line, 1, edge.Site.Line, null, caller.Project))
                },
                new[] { index.Step(caller, "direct", edge.Site), index.Step(factory, "direct") }, "medium",
                new[] { "Ownership by convention: a method that returns a new disposable hands ownership to its caller; the factory may document otherwise.", "Only the declaring member is checked for disposal." },
                "Handles or buffers held by the object stay allocated until the finalizer runs, if there is one. Not measured.",
                $"Wrap the result: 'using var x = {factory.Name}(...)' (or 'await using').",
                "Call the member in a loop and watch handle/connection counts or finalizer activity, or inject a disposal-tracking double.",
                $"{caller.Id}|{edge.SiteId}");
        }
    }

    private static IEnumerable<Finding> NullFlows(GraphIndex index)
    {
        foreach (var flow in index.Graph.NullFlows)
        {
            if (!index.Nodes.TryGetValue(flow.Consumer, out var consumer) || !index.Nodes.TryGetValue(flow.Callee, out var callee)) continue;
            yield return GraphFindings.Create("ATL3105", FindingSeverity.Info, ValidationLevel.StaticRisk,
                $"{callee.Signature} may return null and {consumer.Signature} uses it unchecked",
                $"{callee.ContainingType}.{callee.Signature} declares a nullable return; the compiler's flow analysis reports a possible null {flow.Kind} in {consumer.ContainingType}.{consumer.Signature}.",
                GraphFindings.LocationOf(consumer, flow.Use),
                new[]
                {
                    new FindingEvidence("compiler-diagnostic", $"{flow.DiagnosticId}: {flow.Message}", new FindingLocation(flow.Use.File, flow.Use.Line, 1, flow.Use.Line, consumer.Signature, consumer.Project)),
                    new FindingEvidence("nullable-producer", $"The value comes from the call to {callee.Signature}, declared with a nullable return type.", new FindingLocation(flow.Producer.File, flow.Producer.Line, 1, flow.Producer.Line, consumer.Signature, consumer.Project))
                },
                new[] { index.Step(consumer, "direct", flow.Use), index.Step(callee, "direct") }, "medium",
                new[] { "The flow analysis is the compiler's and is per method: it knows the declared nullability, not whether the callee returns null at run time.", "Nullable annotations that are wrong or missing hide real cases." },
                "A null at run time would raise NullReferenceException (or pass null into a non-null parameter) in the consumer.",
                "Check for null before use, or make the callee return a non-null result (empty object, exception, or a Try-pattern).",
                "Call the producer with an input for which it returns null and observe the consumer.",
                $"{consumer.Id}|{callee.Id}|{flow.Use.Line}|{flow.DiagnosticId}");
        }
    }

    private sealed record ChainLink(string Id, GraphEdge? Edge, string Resolution);

    /// <summary>The nodes from <paramref name="start"/> down to its seed following the reverse-BFS parents, each with the edge that leaves it.</summary>
    private static List<ChainLink> ChainToSeed(GraphIndex index, ReachResult reach, string start)
    {
        var chain = new List<ChainLink>();
        var current = start;
        var guard = 0;
        while (reach.Visited.TryGetValue(current, out var reached) && guard++ < 128)
        {
            chain.Add(new ChainLink(current, reached.Via, reached.Via is null ? "direct" : GraphIndex.ResolutionLabel(reached.Via)));
            if (reached.ParentId is null) break;
            current = reached.ParentId;
        }
        return chain;
    }
}
