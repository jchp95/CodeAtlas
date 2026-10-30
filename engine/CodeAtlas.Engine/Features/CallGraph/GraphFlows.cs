using System.Security.Cryptography;
using System.Text;

sealed record GraphFlowReport(int SchemaVersion, string Source, GraphCompleteness GraphCompleteness, BusinessFlow[] Flows, string[] Notes);

/// <summary>
/// Business flows projected from the shared call graph into the existing <see cref="BusinessFlow"/> contract.
/// Steps are limited to what the graph proves or enumerates: resolved calls, possible dispatch targets, lambdas, and
/// calls that reach recognizable persistence, HTTP or message-broker APIs. Nothing is inferred from method names.
/// A flow is a statically detected path; it says nothing about which path ran.
/// </summary>
static class GraphFlows
{
    private const int MaxSteps = 60;

    public static GraphFlowReport Project(GraphIndex index, GraphLimits limits)
    {
        var flows = new List<BusinessFlow>();
        foreach (var entry in index.Graph.EntryPoints)
        {
            if (!index.Nodes.TryGetValue(entry.NodeId, out var root)) continue;
            var steps = new List<BusinessFlowStep>
            {
                new(0, entry.Kind == "message-consumer" ? "message-consumer" : "endpoint", entry.Label, entry.File, Math.Max(1, entry.Line), "declared",
                    "Entry point detected in source; availability at run time is not verified.")
            };

            var visited = new HashSet<string>(StringComparer.Ordinal) { root.Id };
            var queue = new Queue<(string Id, int Depth)>();
            queue.Enqueue((root.Id, 0));
            var truncated = false;
            var unresolved = 0;
            while (queue.Count > 0 && !truncated)
            {
                var (current, depth) = queue.Dequeue();
                foreach (var edge in index.OutEdges(current).OrderBy(e => e.Site.File, StringComparer.Ordinal).ThenBy(e => e.Site.Line).ThenBy(e => e.To, StringComparer.Ordinal))
                {
                    if (steps.Count >= MaxSteps) { truncated = true; break; }
                    var step = StepFor(index, edge, steps.Count);
                    if (edge.To is null) unresolved++;
                    if (step is not null) steps.Add(step);
                    if (edge.To is null || depth >= limits.MaxDepth || !visited.Add(edge.To) || !index.Nodes.TryGetValue(edge.To, out var target) || target.IsExternal) continue;
                    queue.Enqueue((edge.To, depth + 1));
                }
            }

            var inferred = steps.Skip(1).Any(s => s.Confidence != "resolved");
            var material = $"{root.Project}|{entry.Label}|{entry.File}|{entry.Kind}|graph";
            var id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material))).ToLowerInvariant()[..20];
            flows.Add(new BusinessFlow(id, root.Project, root.Project, entry.Label, entry.Label, entry.File, Math.Max(1, entry.Line),
                inferred ? "inferred" : "source-traced",
                $"{steps.Count - 1} step(s) reachable in the call graph{(unresolved > 0 ? $"; {unresolved} call(s) could not be resolved" : "")}{(truncated ? $"; stopped at {MaxSteps} steps" : "")}. Static paths, not an observed execution.",
                steps.ToArray()));
        }

        return new GraphFlowReport(1, "callGraph", index.Graph.Completeness, flows.OrderBy(f => f.Name, StringComparer.Ordinal).ToArray(),
            new[] { "Steps come from resolved symbols; dispatch targets are 'candidate' because the receiver at run time may differ.", "No business meaning is derived from method names." });
    }

    private static BusinessFlowStep? StepFor(GraphIndex index, GraphEdge edge, int sequence)
    {
        if (edge.To is null)
            return new BusinessFlowStep(sequence, edge.Resolution == EdgeResolution.Delegate ? "delegate-invocation" : "unresolved-call", edge.Label ?? "(unknown)",
                edge.Site.File, edge.Site.Line, "candidate", edge.Reason ?? "The target is not statically known.");

        var target = index.Nodes[edge.To];
        // Auto-property accessors, implicit constructors and initializer plumbing carry no behavior worth a step of their own.
        if (!target.IsExternal && (target.Kind == "initializer" || target.Tags.Contains("implicit") || target.Kind == "accessor") && index.OutEdges(target.Id).Count == 0) return null;
        var symbol = $"{Short(target.ContainingType)}.{target.Name}";
        if (target.IsExternal)
        {
            var remote = target.Tags.FirstOrDefault(t => t.StartsWith("remote:", StringComparison.Ordinal));
            if (remote is null) return null;
            var kind = remote switch { "remote:Database" => "persistence-call", "remote:HTTP" => "client-call", _ => "message-publish" };
            return new BusinessFlowStep(sequence, kind, symbol, edge.Site.File, edge.Site.Line, "resolved",
                "Call to a recognized external API; the effect at run time is not verified.");
        }

        return edge switch
        {
            { Resolution: EdgeResolution.InterfaceDispatch, Certainty: EdgeCertainty.Potential } => Step("service-implementation", "candidate", "Possible implementation of an interface call; other implementations may exist."),
            { Resolution: EdgeResolution.VirtualDispatch, Certainty: EdgeCertainty.Potential } => Step("override", "candidate", "Possible override reached by virtual dispatch."),
            { Resolution: EdgeResolution.InterfaceDispatch or EdgeResolution.VirtualDispatch } => Step("dependency-call", "resolved", "Call through a declared abstraction; the concrete target is listed separately."),
            { Resolution: EdgeResolution.MediatorDispatch } => Step("request-handler", "candidate", "IRequestHandler implementation for the request type sent here."),
            { Resolution: EdgeResolution.SyntaxInferred } => Step("method-call", "inferred", "Roslyn could not bind this call; matched by receiver type name, method name and arity."),
            { Resolution: EdgeResolution.Lambda or EdgeResolution.Delegate } => Step("lambda", "candidate", "A delegate created here; whether and when it runs is decided by its receiver."),
            { Certainty: EdgeCertainty.Proven } => Step("method-call", "resolved", "Call resolved to a symbol by Roslyn."),
            _ => null
        };

        BusinessFlowStep Step(string kind, string confidence, string detail) =>
            new(sequence, kind, symbol, target.Location?.File ?? edge.Site.File, target.Location?.Line ?? edge.Site.Line, confidence, detail);
    }

    private static string Short(string? type) => type is null ? "" : type.Contains('.') ? type[(type.LastIndexOf('.') + 1)..] : type;
}
