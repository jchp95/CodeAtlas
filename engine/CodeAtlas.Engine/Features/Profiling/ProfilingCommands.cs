using System.Text.Json;

/// <summary>
/// runtime doctor | profile cpu|counters|heap|compare. Exit codes: 0 report produced, 2 usage, 5 diagnostic tool missing,
/// 7 execution/attach not authorized, 130 cancelled. Profiling launches or inspects a live process, so it needs the same explicit consent as debugging.
/// </summary>
static class ProfilingCommands
{
    public const string Usage = """
  runtime doctor
  profile cpu <repository-path> --program PATH/App.dll [--args "a b"] --allow-execution [--duration N] [--repeat N] [--hot-threshold 0.25] [--timeout-seconds N] [--out FILE]
  profile counters <repository-path> --program PATH/App.dll --allow-execution [--duration N] [--refresh-seconds N] [--out FILE]
  profile heap <repository-path> --program PATH/App.dll --allow-execution [--snapshots N] [--interval-seconds N] [--warmup-seconds N] [--out FILE]
  profile <cpu|counters|heap> <repository-path> --attach PID --allow-attach ...
  profile compare <baseline-report.json> <current-report.json>
""";

    private static readonly string[] ValueOptions = { "--program", "--args", "--attach", "--duration", "--repeat", "--hot-threshold", "--timeout-seconds", "--out", "--refresh-seconds", "--snapshots", "--interval-seconds", "--warmup-seconds" };

    public static int Run(string command, string[] args)
    {
        var json = JsonDefaults.Options;
        var positional = new List<string>();
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i].StartsWith("--", StringComparison.Ordinal)) { if (ValueOptions.Contains(args[i])) i++; continue; }
            positional.Add(args[i]);
        }
        var sub = positional.FirstOrDefault() ?? "";

        if (command == "runtime" && sub == "doctor")
        {
            var tools = ProfilingTools.Doctor();
            Console.Write(JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                status = tools.All(t => t.Available) ? "ready" : "toolsMissing",
                tools,
                install = tools.Any(t => !t.Available) ? ProfilingTools.InstallHint : null,
                notStarted = new[] { "dotnet-dump (process dumps) is not used: dumps contain process memory and need a separate, explicit privacy decision.", "dotnet-monitor and OpenTelemetry collection: Phase 6." }
            }, json));
            return tools.All(t => t.Available) ? 0 : 5;
        }
        if (command != "profile") { Console.Error.WriteLine(Usage); return 2; }

        if (sub == "compare")
        {
            if (positional.Count < 3 || !File.Exists(positional[1]) || !File.Exists(positional[2])) { Console.Error.WriteLine(Usage); return 2; }
            try { Console.Write(JsonSerializer.Serialize(ProfileComparer.Compare(positional[1], positional[2]), json)); return 0; }
            catch (InvalidOperationException ex) { Console.Error.WriteLine(ex.Message); return 2; }
        }
        if (sub is not ("cpu" or "counters" or "heap") || positional.Count < 2 || !Directory.Exists(Path.GetFullPath(positional[1]))) { Console.Error.WriteLine(Usage); return 2; }

        var root = PathTools.Canonical(Path.GetFullPath(positional[1]));
        var program = GraphCommands.OptionValue(args, "--program");
        var attach = GraphCommands.IntOption(args, "--attach");
        if ((program is null) == (attach is null)) { Console.Error.WriteLine("Pass exactly one of --program or --attach.\n" + Usage); return 2; }
        if ((attach is null && !args.Contains("--allow-execution")) || (attach is not null && !args.Contains("--allow-attach")))
        {
            Console.Write(JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                status = "authorizationRequired",
                message = attach is null
                    ? "Profiling launches the program under a tracer and runs its code. Pass --allow-execution to proceed."
                    : $"Profiling attaches to process {attach} and observes it (traces, counters or heap snapshots, which may include type names and sizes of its memory). Pass --allow-attach to proceed.",
                program,
                attach
            }, json));
            return 7;
        }

        var target = new ProfileTarget(program is null ? null : PathTools.Canonical(Path.GetFullPath(program)), (GraphCommands.OptionValue(args, "--args") ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries), attach);
        using var cancellation = new ProcessCancellation();
        GraphIndex? index = null;
        try
        {
            var graph = new CallGraphBuilder(root, new CallGraphOptions(new GraphLimits())).Build(cancellation.Token);
            if (graph.Status != GraphStatus.Cancelled) index = new GraphIndex(graph);
        }
        catch (Exception) { /* profiling still works without source mapping; methods then stay unresolved */ }

        var timeout = GraphCommands.IntOption(args, "--timeout-seconds") ?? 300;
        string text; string status;
        switch (sub)
        {
            case "cpu":
            {
                var report = CpuProfiler.Run(root, index, new CpuOptions(target, Math.Clamp(GraphCommands.IntOption(args, "--duration") ?? 0, 0, 600), Math.Clamp(GraphCommands.IntOption(args, "--repeat") ?? 1, 1, 10), timeout,
                    double.TryParse(GraphCommands.OptionValue(args, "--hot-threshold"), System.Globalization.CultureInfo.InvariantCulture, out var hot) ? hot : 0.25), cancellation.Token);
                text = JsonSerializer.Serialize(report, json); status = report.Status; break;
            }
            case "counters":
            {
                var report = CounterProfiler.Run(root, index, new CounterOptions(target, Math.Clamp(GraphCommands.IntOption(args, "--duration") ?? 15, 2, 600), Math.Clamp(GraphCommands.IntOption(args, "--refresh-seconds") ?? 1, 1, 30), timeout), cancellation.Token);
                text = JsonSerializer.Serialize(report, json); status = report.Status; break;
            }
            default:
            {
                var report = HeapProfiler.Run(root, index, new HeapOptions(target, Math.Clamp(GraphCommands.IntOption(args, "--snapshots") ?? 2, 1, 6), Math.Clamp(GraphCommands.IntOption(args, "--interval-seconds") ?? 5, 1, 120),
                    Math.Clamp(GraphCommands.IntOption(args, "--warmup-seconds") ?? 3, 0, 120), timeout), cancellation.Token);
                text = JsonSerializer.Serialize(report, json); status = report.Status; break;
            }
        }
        if (GraphCommands.OptionValue(args, "--out") is { } output) File.WriteAllText(output, text); else Console.Write(text);
        return status == "cancelled" ? 130 : status == "toolMissing" ? 5 : 0;
    }
}
