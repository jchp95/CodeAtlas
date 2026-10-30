using System.Text.Json;

/// <summary>
/// tests discover|related|run|compare and exceptions resolve. Exit codes: 0 report produced, 2 usage, 3 symbol not found, 4 nothing to run,
/// 6 ambiguous symbol, 7 execution not authorized, 130 cancelled.
/// </summary>
static class TestingCommands
{
    public const string Usage = """
  tests discover <repository-path>
  tests related <repository-path> <symbol>
  tests run <repository-path> --allow-execution [--project NAME] [--filter EXPR] [--related SYMBOL] [--failed-from REPORT.json] [--no-build] [--timeout-seconds N] [--out FILE]
  tests compare <baseline-report.json> <current-report.json>
  exceptions resolve <repository-path> --trace-file FILE|-
  (running tests executes the code under test: it requires --allow-execution)
""";

    private static readonly string[] ValueOptions = { "--project", "--filter", "--related", "--failed-from", "--timeout-seconds", "--out", "--trace-file", "--depth" };

    public static int Run(string command, string[] args)
    {
        var positional = Positional(args);
        var sub = positional.FirstOrDefault() ?? "";
        positional = positional.Skip(1).ToList();
        var json = JsonDefaults.Options;

        if (command == "tests" && sub == "compare")
        {
            if (positional.Count < 2 || !File.Exists(positional[0]) || !File.Exists(positional[1])) { Console.Error.WriteLine("Usage: tests compare <baseline-report.json> <current-report.json>"); return 2; }
            var a = JsonSerializer.Deserialize<TestRunReport>(File.ReadAllText(positional[0]), json);
            var b = JsonSerializer.Deserialize<TestRunReport>(File.ReadAllText(positional[1]), json);
            if (a is null || b is null) { Console.Error.WriteLine("A report could not be read."); return 2; }
            Console.Write(JsonSerializer.Serialize(TestComparer.Compare(a, b, positional[0], positional[1]), json));
            return 0;
        }

        if (positional.Count == 0 || !Directory.Exists(Path.GetFullPath(positional[0])) || (command == "tests" && sub is not ("discover" or "related" or "run")) || (command == "exceptions" && sub != "resolve"))
        {
            Console.Error.WriteLine(Usage);
            return 2;
        }
        var root = Path.GetFullPath(positional[0]);
        using var cancellation = new ProcessCancellation();
        var limits = new GraphLimits(MaxDepth: Math.Clamp(GraphCommands.IntOption(args, "--depth") ?? 8, 1, 64));
        var graph = new CallGraphBuilder(root, new CallGraphOptions(limits)).Build(cancellation.Token);
        if (graph.Status == GraphStatus.Cancelled) return 130;
        var index = new GraphIndex(graph);

        switch (command, sub)
        {
            case ("tests", "discover"):
                Console.Write(JsonSerializer.Serialize(TestDiscovery.Report(index), json));
                return 0;
            case ("tests", "related"):
                if (positional.Count < 2) { Console.Error.WriteLine(Usage); return 2; }
                var impact = ImpactAnalyzer.Analyze(index, positional[1], limits, null);
                var related = impact.RelatedTests.Select(t => index.Nodes[t.Node.Id]).Select(n => (n.ContainingType ?? "", n.Name)).ToList();
                Console.Write(JsonSerializer.Serialize(new
                {
                    schemaVersion = 1,
                    status = impact.Status,
                    symbol = impact.Symbol,
                    matches = impact.Matches,
                    tests = impact.RelatedTests,
                    filter = related.Count == 0 ? null : TestDiscovery.Filter(related),
                    exhaustive = impact.Exhaustive,
                    statement = impact.Statement
                }, json));
                return impact.Status == "ok" ? 0 : impact.Status == "ambiguous" ? 6 : 3;
            case ("tests", "run"):
                var options = new TestRunOptions(GraphCommands.OptionValue(args, "--project"), GraphCommands.OptionValue(args, "--filter"), GraphCommands.OptionValue(args, "--related"),
                    GraphCommands.OptionValue(args, "--failed-from"), args.Contains("--no-build"), GraphCommands.IntOption(args, "--timeout-seconds") ?? 600);
                if (!args.Contains("--allow-execution"))
                {
                    var (projects, filter, notes, error) = TestRunner.Plan(root, index, options);
                    Console.Write(JsonSerializer.Serialize(new
                    {
                        schemaVersion = 1,
                        status = "authorizationRequired",
                        message = "Running tests executes the code of the selected projects. Review the plan and pass --allow-execution to proceed.",
                        projects = projects.Select(p => p.ProjectKey),
                        filter,
                        error,
                        notes
                    }, json));
                    return 7;
                }
                var report = TestRunner.Run(root, index, options, cancellation.Token);
                var text = JsonSerializer.Serialize(report, json);
                Directory.CreateDirectory(report.ResultsDirectory);
                File.WriteAllText(Path.Combine(report.ResultsDirectory, "report.json"), text);
                if (GraphCommands.OptionValue(args, "--out") is { } output) File.WriteAllText(output, text); else Console.Write(text);
                return report.Status == "cancelled" ? 130 : report.Status == "nothingToRun" ? 4 : 0;
            case ("exceptions", "resolve"):
                var traceFile = GraphCommands.OptionValue(args, "--trace-file");
                if (traceFile is null) { Console.Error.WriteLine("--trace-file FILE (or -) is required."); return 2; }
                var trace = traceFile == "-" ? Console.In.ReadToEnd() : File.ReadAllText(traceFile);
                Console.Write(JsonSerializer.Serialize(ExceptionAnalyzer.Analyze(root, index, trace, limits), json));
                return 0;
        }
        return 2;
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
}
