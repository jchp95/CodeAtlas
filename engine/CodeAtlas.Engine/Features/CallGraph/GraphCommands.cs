using System.Text.Json;

/// <summary>
/// CLI surface of the graph features. Exit codes: 0 success, 2 usage, 3 symbol not found, 6 ambiguous symbol, 130 cancelled.
/// Every command prints one JSON document with <c>schemaVersion</c> on stdout; diagnostics go to stderr.
/// </summary>
static class GraphCommands
{
    public const string Usage = """
  graph build <repository-path> [--no-cache] [--max-dispatch-targets N] [--max-parallelism N] [--out FILE]
  graph calls <repository-path> <symbol> [--direction callers|callees] [--depth N]
  graph cycles <repository-path>
  graph flows <repository-path>
  graph analyze <repository-path>
  architecture-rules <repository-path>
  architecture-check <repository-path>
  impact <repository-path> <symbol> [--depth N] [--project NAME]
  (a <symbol> is a node id N-…, a position path/File.cs:LINE[:COL], or a name such as Type.Method)
""";

    private static readonly string[] ValueOptions = { "--max-dispatch-targets", "--depth", "--direction", "--out", "--project", "--max-results", "--max-parallelism" };

    public static int Run(string command, string[] args)
    {
        var positional = Positional(args);
        var sub = command == "graph" ? positional.FirstOrDefault() ?? "" : "";
        if (command == "graph") positional = positional.Skip(1).ToList();
        if (positional.Count == 0 || !Directory.Exists(Path.GetFullPath(positional[0])))
        {
            Console.Error.WriteLine(positional.Count == 0 ? "A repository path is required." : $"Repository directory does not exist: {Path.GetFullPath(positional[0])}");
            return 2;
        }
        var root = Path.GetFullPath(positional[0]);
        var json = JsonDefaults.Options;

        if (command == "architecture-rules")
        {
            var config = ArchitectureConfig.Load(root);
            Console.Write(JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                status = config.Invalid ? "invalidConfiguration" : config.FilePath is null ? "notConfigured" : "configured",
                configFile = config.FilePath,
                layers = config.Layers,
                rules = config.Rules,
                exceptions = config.Exceptions,
                ruleTypes = ArchitectureConfig.RuleTypes,
                ruleDescriptors = GraphFindings.Descriptors.Where(d => d.Id.StartsWith("ATL30", StringComparison.Ordinal)),
                warnings = config.Warnings
            }, json));
            return 0;
        }

        var selector = positional.Count > 1 ? positional[1] : null;
        if ((command == "impact" || sub == "calls") && selector is null)
        {
            Console.Error.WriteLine("A symbol selector is required.\n" + Usage);
            return 2;
        }
        if (command == "graph" && sub is not ("build" or "calls" or "cycles" or "flows" or "analyze"))
        {
            Console.Error.WriteLine(Usage);
            return 2;
        }

        using var cancellation = new ProcessCancellation();
        var limits = new GraphLimits(
            MaxDispatchTargets: IntOption(args, "--max-dispatch-targets") ?? 25,
            MaxDepth: Math.Clamp(IntOption(args, "--depth") ?? 8, 1, 64),
            MaxResults: Math.Clamp(IntOption(args, "--max-results") ?? 500, 1, 100_000));
        var graph = new CallGraphBuilder(root, new CallGraphOptions(limits, !args.Contains("--no-cache"), IntOption(args, "--max-parallelism"))).Build(cancellation.Token);
        var cancelled = graph.Status == GraphStatus.Cancelled;
        var index = new GraphIndex(graph);

        switch (command, sub)
        {
            case ("graph", "build"):
                var text = JsonSerializer.Serialize(graph, json);
                if (OptionValue(args, "--out") is { } output) File.WriteAllText(output, text); else Console.Write(text);
                return cancelled ? 130 : 0;
            case ("graph", "cycles"):
                Console.Write(JsonSerializer.Serialize(CycleAnalysis.Analyze(index, limits.MaxResults), json));
                return cancelled ? 130 : 0;
            case ("graph", "flows"):
                Console.Write(JsonSerializer.Serialize(GraphFlows.Project(index, limits), json));
                return cancelled ? 130 : 0;
            case ("graph", "analyze"):
                Console.Write(JsonSerializer.Serialize(InterproceduralRules.Run(index, limits, cancellation.Token), json));
                return cancelled ? 130 : 0;
            case ("architecture-check", _):
                Console.Write(JsonSerializer.Serialize(ArchitectureEvaluator.Evaluate(index, ArchitectureConfig.Load(root), limits.MaxResults), json));
                return cancelled ? 130 : 0;
            case ("impact", _):
                var impact = ImpactAnalyzer.Analyze(index, selector!, limits, ArchitectureConfig.Load(root), OptionValue(args, "--project"));
                Console.Write(JsonSerializer.Serialize(impact, json));
                return cancelled ? 130 : impact.Status == "ok" ? 0 : impact.Status == "ambiguous" ? 6 : 3;
            case ("graph", "calls"):
                return Calls(index, selector!, OptionValue(args, "--direction"), limits, OptionValue(args, "--project"), cancelled);
            default:
                Console.Error.WriteLine(Usage);
                return 2;
        }
    }

    private sealed record CallItem(NodeSummary Node, int Depth, string Certainty, string Resolution, GraphLocation? CallSite);

    private static int Calls(GraphIndex index, string selector, string? direction, GraphLimits limits, string? project, bool cancelled)
    {
        var selection = SymbolSelector.Resolve(index, selector, project);
        NodeSummary Summary(GraphNode n) => new(n.Id, $"{n.ContainingType}.{n.Signature}".TrimStart('.'), n.Kind, n.Project, n.Location, n.Tags);
        if (selection.Status != SelectorStatus.Found)
        {
            Console.Write(JsonSerializer.Serialize(new { schemaVersion = 1, status = selection.Status == SelectorStatus.Ambiguous ? "ambiguous" : "notFound", selector, matches = selection.Matches.Select(Summary), note = selection.Note }, JsonDefaults.Options));
            return selection.Status == SelectorStatus.Ambiguous ? 6 : 3;
        }

        var subject = selection.Matches[0];
        List<CallItem> Collect(bool forward)
        {
            var all = GraphTraversal.Reach(index, new[] { subject.Id }, forward, limits.MaxDepth, limits.MaxResults, definiteOnly: false);
            var definite = GraphTraversal.Reach(index, new[] { subject.Id }, forward, limits.MaxDepth, limits.MaxResults, definiteOnly: true);
            return all.Visited.Values.Where(r => r.NodeId != subject.Id).OrderBy(r => r.Depth).ThenBy(r => r.NodeId, StringComparer.Ordinal)
                .Select(r => new CallItem(Summary(index.Nodes[r.NodeId]), r.Depth, definite.Visited.ContainsKey(r.NodeId) ? "proven" : "potential", r.Via?.Resolution.ToString() ?? "", r.Via?.Site)).ToList();
        }

        var wantCallers = direction is null or "callers";
        var wantCallees = direction is null or "callees";
        Console.Write(JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            status = cancelled ? "cancelled" : "ok",
            symbol = Summary(subject),
            maxDepth = limits.MaxDepth,
            callers = wantCallers ? Collect(false) : null,
            callees = wantCallees ? Collect(true) : null,
            unresolvedCallSitesFromSymbol = index.OutEdges(subject.Id).Where(e => e.To is null).Select(e => new { e.Resolution, e.Label, e.Reason, e.Site }),
            graphCompleteness = index.Graph.Completeness,
            limitsApplied = index.Graph.LimitsApplied,
            note = "Items are known callers/callees. 'proven' means reached only through statically bound calls; 'potential' needs a dispatch, delegate or lambda hand-off."
        }, JsonDefaults.Options));
        return cancelled ? 130 : 0;
    }

    private static List<string> Positional(string[] args)
    {
        var result = new List<string>();
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i].StartsWith("--", StringComparison.Ordinal)) { if (ValueOptions.Contains(args[i])) i++; continue; }
            result.Add(args[i]);
        }
        return result;
    }

    public static int? IntOption(string[] arguments, string name) => int.TryParse(OptionValue(arguments, name), out var value) ? value : null;

    public static string? OptionValue(string[] arguments, string name)
    {
        var index = Array.FindIndex(arguments, a => a.Equals(name, StringComparison.OrdinalIgnoreCase));
        return index >= 0 && index + 1 < arguments.Length ? arguments[index + 1] : null;
    }
}
