sealed record AppliedException(string Rule, string From, string To, string Reason, int Occurrences);

sealed record ArchitectureReport(
    int SchemaVersion,
    string Status,
    string? ConfigFile,
    LayerDefinition[] Layers,
    ArchitectureRule[] Rules,
    RuleDescriptor[] RuleDescriptors,
    Finding[] Findings,
    AppliedException[] ExceptionsApplied,
    CycleReport Cycles,
    GraphCompleteness GraphCompleteness,
    GraphLimitNote[] LimitsApplied,
    string[] Warnings);

/// <summary>
/// Checks project references and statically bound calls against the configured rules. Only proven dependencies are
/// judged: a possible dispatch target is not evidence that a layer actually depends on another.
/// </summary>
static class ArchitectureEvaluator
{
    private sealed record Dependency(string FromProject, string ToProject, string? FromType, string ToType, string? FromLayer, string? ToLayer,
        string? FromNamespace, string? ToNamespace, FindingEvidence Evidence, FindingLocation Location, CallPathStep[]? Path, bool IsCall);

    public static ArchitectureReport Evaluate(GraphIndex index, ArchitectureConfig config, int maxResults)
    {
        var cycles = CycleAnalysis.Analyze(index, maxResults);
        var findings = new List<Finding>(cycles.Findings);
        var applied = new Dictionary<ArchitectureException, int>();
        var dependencies = CollectDependencies(index, config).ToList();

        foreach (var rule in config.Rules)
        {
            switch (rule.Type)
            {
                case "forbidden":
                    Judge(rule, dependencies, d => d.FromLayer is not null && rule.From.Contains(d.FromLayer) && d.ToLayer is not null && rule.To.Contains(d.ToLayer), "ATL3010", findings, applied, config);
                    break;
                case "allowedOnly":
                    Judge(rule, dependencies, d => d.FromLayer is not null && rule.From.Contains(d.FromLayer) && d.ToLayer is not null
                        && d.ToLayer != d.FromLayer && !rule.To.Contains(d.ToLayer), "ATL3011", findings, applied, config);
                    break;
                case "forbiddenProjects":
                    Judge(rule, dependencies, d => d.FromProject != d.ToProject && ArchitectureConfig.AnyGlob(rule.From, d.FromProject) && ArchitectureConfig.AnyGlob(rule.To, d.ToProject), "ATL3010", findings, applied, config);
                    break;
                case "moduleBoundary":
                    Judge(rule, dependencies, d => d.IsCall && rule.Namespaces.Length > 0
                        && !ArchitectureConfig.AnyGlob(rule.Namespaces, d.FromNamespace) && ArchitectureConfig.AnyGlob(rule.Namespaces, d.ToNamespace)
                        && !ArchitectureConfig.AnyGlob(rule.PublicNamespaces, d.ToNamespace), "ATL3012", findings, applied, config);
                    break;
                case "noCycles" when rule.Scope == "layers":
                    LayerCycles(rule, dependencies, findings);
                    break;
            }
        }

        var status = config.Invalid ? "invalidConfiguration" : config.FilePath is null ? "notConfigured" : "configured";
        var warnings = config.Warnings.ToList();
        if (status == "notConfigured") warnings.Add("No codeatlas.architecture.json was found: only convention-free checks (call cycles, project-reference cycles) ran.");
        if (!index.Graph.Completeness.Closed)
            warnings.Add("The call graph is not complete (see graphCompleteness); a dependency that goes through an unresolved call, a delegate or an unanalyzed project cannot be checked.");

        return new ArchitectureReport(1, status, config.FilePath, config.Layers.ToArray(), config.Rules.ToArray(),
            GraphFindings.Descriptors.Where(d => d.Id.StartsWith("ATL30", StringComparison.Ordinal)).ToArray(),
            findings.OrderByDescending(f => f.Severity).ThenBy(f => f.RuleId, StringComparer.Ordinal).ThenBy(f => f.Location.File, StringComparer.Ordinal).ThenBy(f => f.Location.Line).ToArray(),
            applied.Select(p => new AppliedException(p.Key.Rule, p.Key.From, p.Key.To, p.Key.Reason, p.Value)).ToArray(),
            cycles, index.Graph.Completeness, index.Graph.LimitsApplied, warnings.ToArray());
    }

    private static IEnumerable<Dependency> CollectDependencies(GraphIndex index, ArchitectureConfig config)
    {
        foreach (var references in index.Graph.ProjectReferences)
            foreach (var target in references.Skip(1))
            {
                var file = index.Graph.ProjectFiles.GetValueOrDefault(references[0]) ?? references[0];
                var line = FindReferenceLine(index, file, target);
                var location = new FindingLocation(file, line, 1, line, references[0], references[0]);
                yield return new Dependency(references[0], target, null, target, config.LayerOfProject(references[0]), config.LayerOfProject(target), null, null,
                    new FindingEvidence("project-reference", $"{references[0]} has a ProjectReference to {target}", location), location, null, false);
            }

        foreach (var edge in index.Graph.Edges.Where(GraphIndex.IsDefinite))
        {
            if (!index.Nodes.TryGetValue(edge.From, out var from) || !index.Nodes.TryGetValue(edge.To!, out var to) || from.IsExternal || to.IsExternal) continue;
            if (from.Project == to.Project && config.LayerOfNode(from) == config.LayerOfNode(to)) continue;
            var location = new FindingLocation(edge.Site.File, edge.Site.Line, 1, edge.Site.Line, $"{from.ContainingType}.{from.Signature}", from.Project);
            yield return new Dependency(from.Project, to.Project, from.ContainingType, to.ContainingType ?? to.Project, config.LayerOfNode(from), config.LayerOfNode(to),
                from.Namespace, to.Namespace,
                new FindingEvidence("call", $"{from.ContainingType}.{from.Signature} → {to.ContainingType}.{to.Signature} ({edge.Resolution})", location),
                location, new[] { index.Step(from, "direct", edge.Site), index.Step(to, "direct") }, true);
        }
    }

    private static void Judge(ArchitectureRule rule, List<Dependency> dependencies, Func<Dependency, bool> violates, string findingRule,
        List<Finding> findings, Dictionary<ArchitectureException, int> applied, ArchitectureConfig config)
    {
        var groups = dependencies.Where(violates).GroupBy(d => (d.FromProject, d.ToProject, d.FromType, d.ToType, d.IsCall))
            .OrderBy(g => g.Key.FromProject, StringComparer.Ordinal).ThenBy(g => g.Key.FromType, StringComparer.Ordinal).ThenBy(g => g.Key.ToType, StringComparer.Ordinal);
        foreach (var group in groups)
        {
            var first = group.First();
            var exception = config.Exceptions.FirstOrDefault(e => (e.Rule == "*" || e.Rule == rule.Id)
                && (ArchitectureConfig.Glob(e.From, first.FromProject) || (first.FromType is not null && ArchitectureConfig.Glob(e.From, first.FromType)))
                && (ArchitectureConfig.Glob(e.To, first.ToProject) || ArchitectureConfig.Glob(e.To, first.ToType)));
            if (exception is not null) { applied[exception] = applied.GetValueOrDefault(exception) + group.Count(); continue; }

            var what = first.IsCall ? $"{Short(first.FromType)} → {Short(first.ToType)}" : $"{first.FromProject} → {first.ToProject}";
            var layers = first.FromLayer is not null && first.ToLayer is not null ? $"{first.FromLayer} → {first.ToLayer}" : $"{first.FromProject} → {first.ToProject}";
            var title = findingRule switch
            {
                "ATL3011" => $"{layers} is not an allowed dependency ({rule.Id}): {what}",
                "ATL3012" => $"{Short(first.FromType)} calls into module '{rule.Module}' outside its public namespaces ({rule.Id}): {Short(first.ToType)}",
                _ => $"{layers} is forbidden ({rule.Id}): {what}"
            };
            findings.Add(GraphFindings.Create(findingRule, rule.Severity, ValidationLevel.StaticProven, title,
                $"Rule '{rule.Id}' ({rule.Type}){(rule.Reason is null ? "" : ": " + rule.Reason)}. {group.Count()} proven dependenc{(group.Count() == 1 ? "y" : "ies")} found.",
                first.Location, group.Take(10).Select(g => g.Evidence).ToArray(), first.Path, "high",
                new[] { "Only dependencies that are statically bound or declared in a .csproj are judged; dispatch targets are not.", "The rule reflects the team's configuration, not an architectural truth the analyzer inferred." },
                rule.Reason ?? "The dependency contradicts the layering the team configured, which makes the layers harder to evolve and test independently.",
                findingRule == "ATL3012" ? "Call the module through one of its public namespaces, or move the shared type there." : "Invert the dependency behind an abstraction owned by the inner layer, or move the code to the layer that may depend on the target. If this one is deliberate, record it under \"exceptions\" with a reason.",
                "Open the evidence locations; each is a reference or call that crosses the forbidden boundary.",
                $"{rule.Id}|{first.FromProject}|{first.FromType}|{first.ToProject}|{first.ToType}"));
        }
    }

    private static void LayerCycles(ArchitectureRule rule, List<Dependency> dependencies, List<Finding> findings)
    {
        var edges = dependencies.Where(d => d.FromLayer is not null && d.ToLayer is not null && d.FromLayer != d.ToLayer).ToArray();
        var layers = edges.SelectMany(d => new[] { d.FromLayer!, d.ToLayer! }).Distinct(StringComparer.Ordinal).ToArray();
        foreach (var component in StronglyConnected.Components(layers, l => edges.Where(d => d.FromLayer == l).Select(d => d.ToLayer!)))
        {
            if (component.Count < 2) continue;
            var members = component.ToHashSet(StringComparer.Ordinal);
            var evidence = edges.Where(d => members.Contains(d.FromLayer!) && members.Contains(d.ToLayer!))
                .GroupBy(d => (d.FromLayer, d.ToLayer)).Select(g => g.First().Evidence with { Description = $"{g.Key.FromLayer} → {g.Key.ToLayer}: {g.First().Evidence.Description} (+{g.Count() - 1} more)" }).ToArray();
            findings.Add(GraphFindings.Create("ATL3003", rule.Severity, ValidationLevel.StaticProven, $"Layer cycle ({rule.Id}): {string.Join(" ⇄ ", component)}",
                "These layers depend on each other, directly or transitively, through project references or statically bound calls.",
                evidence[0].Location!, evidence, null, "high",
                new[] { "Only proven dependencies count; layers connected only through dispatch are not reported." },
                "Layers in a cycle cannot be built, tested or replaced independently.",
                "Pick the dependency that points against the intended direction and invert it behind an abstraction.",
                "Open the evidence locations; each pair shows one dependency of the cycle.", $"{rule.Id}|{string.Join("|", component)}"));
        }
    }

    private static int FindReferenceLine(GraphIndex index, string file, string targetProject)
    {
        try
        {
            var targetFile = Path.GetFileName(index.Graph.ProjectFiles.GetValueOrDefault(targetProject) ?? targetProject);
            var lines = File.ReadAllLines(Path.Combine(index.Graph.RepositoryPath, file));
            var found = Array.FindIndex(lines, l => l.Contains("ProjectReference", StringComparison.Ordinal) && l.Contains(targetFile, StringComparison.OrdinalIgnoreCase));
            return found >= 0 ? found + 1 : 1;
        }
        catch { return 1; }
    }

    private static string Short(string? type) => type is null ? "" : type.Contains('.') ? type[(type.LastIndexOf('.') + 1)..] : type;
}
