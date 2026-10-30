sealed record CallCycle(string Kind, string[] Nodes, string[] Edges, string[] Types, bool Definite);

sealed record ProjectCycle(string[] Projects);

sealed record CycleReport(
    int SchemaVersion,
    GraphCompleteness GraphCompleteness,
    CallCycle[] CallCycles,
    CallCycle[] LegitimateRecursion,
    ProjectCycle[] ProjectCycles,
    Finding[] Findings,
    string[] Notes);

/// <summary>
/// Cycle detection over the call graph and the project-reference graph.
/// Recursion (a member reaching itself, or members of one type reaching each other) is reported separately and does
/// not produce findings; a cycle that crosses types is a dependency cycle between those types.
/// </summary>
static class CycleAnalysis
{
    public static CycleReport Analyze(GraphIndex index, int maxResults)
    {
        var notes = new List<string>();
        var source = index.Graph.Nodes.Where(n => !n.IsExternal).Select(n => n.Id).ToHashSet(StringComparer.Ordinal);
        var findings = new List<Finding>();
        var cycles = new List<CallCycle>();
        var recursion = new List<CallCycle>();

        IEnumerable<string> Successors(string id, bool definiteOnly) => index.OutEdges(id)
            .Where(e => e.To is not null && source.Contains(e.To) && (!definiteOnly || GraphIndex.IsDefinite(e))).Select(e => e.To!);

        // 1. Cycles that exist through statically bound calls only.
        var definiteSets = new List<HashSet<string>>();
        foreach (var component in StronglyConnected.Components(source, id => Successors(id, true)))
        {
            if (component.Count == 1)
            {
                var self = index.OutEdges(component[0]).Where(e => e.To == component[0] && GraphIndex.IsDefinite(e)).ToArray();
                if (self.Length > 0) recursion.Add(new CallCycle("directRecursion", component.ToArray(), self.Select(e => e.Id).ToArray(), new[] { index.TypeOf(index.Nodes[component[0]]) }, true));
                continue;
            }
            var types = component.Select(id => index.TypeOf(index.Nodes[id])).Distinct(StringComparer.Ordinal).OrderBy(t => t, StringComparer.Ordinal).ToArray();
            var path = FindCycle(index, component, definiteOnly: true);
            definiteSets.Add(component.ToHashSet(StringComparer.Ordinal));
            if (types.Length == 1)
            {
                recursion.Add(new CallCycle("mutualRecursion", component.ToArray(), path.Select(e => e.Id).ToArray(), types, true));
                continue;
            }
            cycles.Add(new CallCycle("crossTypeCycle", component.ToArray(), path.Select(e => e.Id).ToArray(), types, true));
            findings.Add(CallCycleFinding(index, path, types, definite: true, component.Count));
        }

        // 2. Cycles that need a dispatch / delegate / lambda edge to close: possible, not proven.
        foreach (var component in StronglyConnected.Components(source, id => Successors(id, false)))
        {
            if (component.Count < 2) continue;
            var set = component.ToHashSet(StringComparer.Ordinal);
            if (definiteSets.Any(d => d.SetEquals(set))) continue;
            var types = component.Select(id => index.TypeOf(index.Nodes[id])).Distinct(StringComparer.Ordinal).OrderBy(t => t, StringComparer.Ordinal).ToArray();
            if (types.Length < 2) continue;
            var path = FindCycle(index, component, definiteOnly: false);
            cycles.Add(new CallCycle("possibleCrossTypeCycle", component.ToArray(), path.Select(e => e.Id).ToArray(), types, false));
            findings.Add(CallCycleFinding(index, path, types, definite: false, component.Count));
        }

        var projectCycles = ProjectCycles(index, findings);
        if (cycles.Count > maxResults)
        {
            notes.Add($"Only the first {maxResults} call cycles are listed ({cycles.Count} found); raise --depth/limits or narrow the scope to see the rest.");
            cycles = cycles.Take(maxResults).ToList();
            findings = findings.Take(maxResults + projectCycles.Length).ToList();
        }
        if (!index.Graph.Completeness.Closed)
            notes.Add("Cycles through unresolved calls, delegate invocations or targets outside the solution cannot be detected; absence of a cycle is not a proof that none exists.");

        return new CycleReport(1, index.Graph.Completeness, cycles.ToArray(), recursion.ToArray(), projectCycles, findings.ToArray(), notes.ToArray());
    }

    private static List<GraphEdge> FindCycle(GraphIndex index, List<string> component, bool definiteOnly)
    {
        var members = component.ToHashSet(StringComparer.Ordinal);
        var start = component[0];
        var parent = new Dictionary<string, GraphEdge>(StringComparer.Ordinal);
        var queue = new Queue<string>();
        queue.Enqueue(start);
        var seen = new HashSet<string>(StringComparer.Ordinal) { start };
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            foreach (var edge in index.OutEdges(current).Where(e => e.To is not null && members.Contains(e.To) && (!definiteOnly || GraphIndex.IsDefinite(e)))
                         .OrderBy(e => e.To, StringComparer.Ordinal))
            {
                if (edge.To == start)
                {
                    var path = new List<GraphEdge> { edge };
                    for (var at = current; at != start; at = parent[at].From) path.Insert(0, parent[at]);
                    return path;
                }
                if (seen.Add(edge.To!)) { parent[edge.To!] = edge; queue.Enqueue(edge.To!); }
            }
        }
        return new List<GraphEdge>();
    }

    private static Finding CallCycleFinding(GraphIndex index, List<GraphEdge> path, string[] types, bool definite, int memberCount)
    {
        var first = index.Nodes[path[0].From];
        var steps = path.Select(e => index.Step(index.Nodes[e.From], GraphIndex.ResolutionLabel(e), e.Site)).ToList();
        steps.Add(index.Step(first, "direct"));
        var evidence = path.Select(e => new FindingEvidence("call",
            $"{index.Nodes[e.From].ContainingType}.{index.Nodes[e.From].Signature} → {(e.To is null ? e.Label : index.Nodes[e.To].ContainingType + "." + index.Nodes[e.To].Signature)} ({e.Resolution}, {e.Certainty})",
            new FindingLocation(e.Site.File, e.Site.Line, 1, e.Site.Line, null, index.Nodes[e.From].Project))).ToArray();
        return GraphFindings.Create("ATL3001", definite ? FindingSeverity.Warning : FindingSeverity.Info,
            definite ? ValidationLevel.StaticProven : ValidationLevel.StaticRisk,
            definite ? $"Call cycle across {types.Length} types: {string.Join(" ⇄ ", types.Select(ShortName))}" : $"Possible call cycle across {types.Length} types: {string.Join(" ⇄ ", types.Select(ShortName))}",
            definite
                ? $"Following only statically bound calls, control can return to {first.ContainingType}.{first.Signature}; the strongly connected group has {memberCount} members."
                : "The cycle exists only if an interface/virtual call, delegate or lambda reaches the listed target; the call graph lists it as possible, not demonstrated.",
            GraphFindings.LocationOf(first, path[0].Site), evidence, steps.ToArray(), definite ? "high" : "low",
            definite
                ? new[] { "A cyclic call chain is not always a defect: it can be legitimate mutual recursion or a callback protocol.", "Unresolved calls elsewhere may hide further cycles." }
                : new[] { "At least one edge is a possible dispatch target; the actual receiver at run time may never take this path.", "Visitor, composite and observer patterns produce this shape legitimately." },
            "Types that call each other cannot be understood, tested or replaced independently, and changes to one ripple into the other.",
            "Break the cycle at the weakest edge: invert one dependency behind an interface owned by the lower-level type, move shared behavior to a third type, or pass data instead of calling back.",
            "Open each call site in the evidence in order; for the possible variant, confirm with a test or trace that the dispatched target is actually reached.",
            string.Join("|", types));
    }

    private static ProjectCycle[] ProjectCycles(GraphIndex index, List<Finding> findings)
    {
        var references = index.Graph.ProjectReferences.ToDictionary(r => r[0], r => r.Skip(1).ToArray(), StringComparer.Ordinal);
        var result = new List<ProjectCycle>();
        foreach (var component in StronglyConnected.Components(references.Keys, p => references[p].Where(references.ContainsKey)))
        {
            if (component.Count < 2) continue;
            var members = component.ToHashSet(StringComparer.Ordinal);
            result.Add(new ProjectCycle(component.ToArray()));
            var edges = component.SelectMany(p => references[p].Where(members.Contains).Select(r => (From: p, To: r))).ToArray();
            var evidence = edges.Select(e => ProjectEvidence(index, e.From, e.To)).ToArray();
            var anchor = edges[0];
            findings.Add(GraphFindings.Create("ATL3002", FindingSeverity.Error, ValidationLevel.StaticProven,
                $"Project reference cycle: {string.Join(" ⇄ ", component)}",
                "These projects reference each other through <ProjectReference> items, directly or transitively.",
                evidence[0].Location!, evidence, null, "high",
                new[] { "Only references that resolve to a project file inside the analyzed directory are considered." },
                "MSBuild rejects circular project references, so the solution cannot be built as declared; code across the cyclic edge cannot be bound by this analysis either.",
                "Remove one reference by extracting the shared contracts into a lower-level project that both depend on.",
                "Run 'dotnet build' on any project of the cycle; it reports a circular dependency.",
                string.Join("|", component)));
        }
        return result.ToArray();
    }

    private static FindingEvidence ProjectEvidence(GraphIndex index, string from, string to)
    {
        var file = index.Graph.ProjectFiles.GetValueOrDefault(from) ?? from;
        var line = 1;
        try
        {
            var target = Path.GetFileName(index.Graph.ProjectFiles.GetValueOrDefault(to) ?? to);
            var text = File.ReadAllLines(Path.Combine(index.Graph.RepositoryPath, file));
            var found = Array.FindIndex(text, l => l.Contains("ProjectReference", StringComparison.Ordinal) && l.Contains(target, StringComparison.OrdinalIgnoreCase));
            if (found >= 0) line = found + 1;
        }
        catch { /* the line is a navigation aid only */ }
        return new FindingEvidence("project-reference", $"{from} references {to}", new FindingLocation(file, line, 1, line, from, from));
    }

    private static string ShortName(string type) => type.Contains('.') ? type[(type.LastIndexOf('.') + 1)..] : type;
}
