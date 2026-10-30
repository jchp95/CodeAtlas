using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

/// <summary>
/// debug doctor | debug run. 'debug run' is a long-lived session: one JSON command per stdin line, one JSON reply per stdout line.
/// Exit codes: 0 session ended normally, 2 usage, 5 debugger not installed, 7 execution/attach not authorized, 130 cancelled.
/// </summary>
static class DebugCommands
{
    public const string Usage = """
  debug doctor
  debug timeline <recording.jsonl>
  debug run <repository-path> --program PATH/App.dll [--args "a b"] [--cwd DIR] --allow-execution [--stop-timeout-seconds N] [--record FILE]
  debug run <repository-path> --attach PID --allow-attach
  (session: stdin JSON lines {"id":1,"cmd":"breakpoints|functionBreakpoints|exceptions|run|continue|next|stepIn|stepOut|pause|stack|variables|evaluate|threads|terminate",…})
""";

    public static string? FindAdapter()
    {
        var candidates = new List<string?>
        {
            Environment.GetEnvironmentVariable("CODEATLAS_NETCOREDBG"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codeatlas", "tools", "netcoredbg", "netcoredbg")
        };
        candidates.AddRange((Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator).Select(p => Path.Combine(p, "netcoredbg")));
        return candidates.FirstOrDefault(c => !string.IsNullOrWhiteSpace(c) && File.Exists(c));
    }

    public static int Run(string[] args)
    {
        var sub = args.FirstOrDefault() ?? "";
        var json = JsonDefaults.Options;
        if (sub == "doctor")
        {
            var adapter = FindAdapter();
            string? version = null;
            if (adapter is not null)
                try { version = BoundedProcess.Run(adapter, new[] { "--version" }, Environment.CurrentDirectory, TimeSpan.FromSeconds(10), CancellationToken.None, 3).Tail.FirstOrDefault(); } catch { }
            Console.Write(JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                status = adapter is null ? "debuggerNotInstalled" : "ready",
                adapter,
                version,
                instructions = adapter is null ? "Download netcoredbg for your platform from https://github.com/Samsung/netcoredbg/releases, unpack it to ~/.codeatlas/tools/netcoredbg (or set CODEATLAS_NETCOREDBG) and run 'debug doctor' again." : null,
                limits = new[] { "Async task inspection is not offered by the adapter: only async state-machine frames are visible.", "Remote and container debugging are not implemented; local launch and local attach by process id are." }
            }, json));
            return adapter is null ? 5 : 0;
        }
        if (sub == "timeline") return Timeline(args.Skip(1).FirstOrDefault());
        if (sub != "run") { Console.Error.WriteLine(Usage); return 2; }

        var rest = args.Skip(1).ToArray();
        var positional = rest.Where((a, i) => !a.StartsWith("--", StringComparison.Ordinal) && (i == 0 || !new[] { "--program", "--args", "--cwd", "--attach", "--stop-timeout-seconds", "--record" }.Contains(rest[i - 1]))).ToList();
        if (positional.Count == 0 || !Directory.Exists(Path.GetFullPath(positional[0]))) { Console.Error.WriteLine(Usage); return 2; }
        var root = PathTools.Canonical(Path.GetFullPath(positional[0]));
        var program = GraphCommands.OptionValue(rest, "--program");
        var attach = GraphCommands.IntOption(rest, "--attach");
        if ((program is null && attach is null) || (program is not null && attach is not null)) { Console.Error.WriteLine("Pass exactly one of --program or --attach.\n" + Usage); return 2; }

        // Executing or attaching touches a live process: explicit consent only, and attach has its own flag so it is never implied.
        if ((attach is null && !rest.Contains("--allow-execution")) || (attach is not null && !rest.Contains("--allow-attach")))
        {
            Console.Write(JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                status = "authorizationRequired",
                message = attach is null ? "Debugging launches the program and runs its code. Pass --allow-execution to proceed." : $"Attaching pauses and inspects process {attach}, including its memory. Pass --allow-attach to proceed.",
                program,
                attach
            }, json));
            return 7;
        }
        var adapterPath = FindAdapter();
        if (adapterPath is null) { Console.Write(JsonSerializer.Serialize(new { schemaVersion = 1, status = "debuggerNotInstalled", message = "netcoredbg was not found. Run 'debug doctor'." }, json)); return 5; }

        using var cancellation = new ProcessCancellation();
        GraphIndex? index = null;
        try
        {
            var graph = new CallGraphBuilder(root, new CallGraphOptions(new GraphLimits())).Build(cancellation.Token);
            if (graph.Status != GraphStatus.Cancelled) index = new GraphIndex(graph);
        }
        catch (Exception) { /* a session without call-graph resolution is still a session; frames then say 'external' */ }

        var launch = new DebugLaunch(program is null ? null : PathTools.Canonical(Path.GetFullPath(program)), (GraphCommands.OptionValue(rest, "--args") ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries),
            GraphCommands.OptionValue(rest, "--cwd") ?? root, attach, GraphCommands.IntOption(rest, "--stop-timeout-seconds") ?? 120);
        using var session = new DebugSession(root, adapterPath, index, launch);
        // The main thread is blocked reading stdin, so cancellation has to end the process itself (and take the debuggee down with the adapter).
        cancellation.Token.Register(() => { session.Dispose(); Environment.Exit(130); });

        // --record keeps only what the session really captured (stops, exceptions, exits), already redacted, so a failure can be reviewed later.
        // State that was never captured (variables not inspected, earlier history) cannot be reconstructed from it.
        var record = GraphCommands.OptionValue(rest, "--record");
        var recorded = 0;
        void Emit(JsonNode node)
        {
            Console.Out.WriteLine(node.ToJsonString()); Console.Out.Flush();
            if (record is not null && node["result"]?["state"]?.GetValue<string>() is "stopped" or "exited")
                File.AppendAllText(record, new JsonObject { ["seq"] = ++recorded, ["time"] = DateTimeOffset.UtcNow.ToString("o"), ["command"] = node["command"]?.DeepClone(), ["capture"] = node["result"]!.DeepClone() }.ToJsonString() + "\n");
        }
        try { Emit(new JsonObject { ["event"] = "ready", ["state"] = session.Start(), ["graphResolution"] = index is not null }); }
        catch (Exception ex) { Emit(new JsonObject { ["event"] = "error", ["message"] = ex.Message }); return 1; }

        string? line;
        while (!cancellation.Token.IsCancellationRequested && (line = Console.In.ReadLine()) is not null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            JsonObject? command = null;
            try
            {
                command = JsonNode.Parse(line)?.AsObject();
                var result = session.Handle(command!);
                Emit(new JsonObject { ["id"] = command!["id"]?.DeepClone(), ["command"] = command["cmd"]?.DeepClone(), ["ok"] = true, ["result"] = result });
                if (command["cmd"]?.GetValue<string>() == "terminate") return 0;
            }
            catch (Exception ex)
            {
                Emit(new JsonObject { ["id"] = command?["id"]?.DeepClone(), ["ok"] = false, ["error"] = ex.Message });
            }
        }
        return cancellation.Token.IsCancellationRequested ? 130 : 0;
    }

    /// <summary>Summarizes a recording: what was captured, in order, with the frame and exception of each stop.</summary>
    private static int Timeline(string? file)
    {
        if (file is null || !File.Exists(file)) { Console.Error.WriteLine("Usage: debug timeline <recording.jsonl>"); return 2; }
        var events = File.ReadAllLines(file).Where(l => l.Length > 0).Select(l => JsonNode.Parse(l)!).Select(n =>
        {
            var capture = n["capture"]!;
            var frame = capture["frames"]?.AsArray().FirstOrDefault();
            return new JsonObject
            {
                ["seq"] = n["seq"]?.DeepClone(), ["time"] = n["time"]?.DeepClone(), ["trigger"] = n["command"]?.DeepClone(), ["state"] = capture["state"]?.DeepClone(), ["reason"] = capture["reason"]?.DeepClone(),
                ["exitCode"] = capture["exitCode"]?.DeepClone(),
                ["exceptionType"] = capture["exception"]?["typeName"]?.DeepClone(), ["exceptionMessage"] = capture["exception"]?["message"]?.DeepClone(),
                ["file"] = frame?["file"]?.DeepClone(), ["line"] = frame?["line"]?.DeepClone(), ["member"] = frame?["name"]?.DeepClone(), ["nodeId"] = frame?["nodeId"]?.DeepClone(),
                ["hasLocals"] = capture["locals"] is not null
            };
        }).ToArray();
        Console.Write(new JsonObject
        {
            ["schemaVersion"] = 1,
            ["events"] = new JsonArray(events.Select(e => (JsonNode)e).ToArray()),
            ["notes"] = new JsonArray("Only captured stops and exits appear here; values that were not inspected at the time cannot be reconstructed.")
        }.ToJsonString(JsonDefaults.Options));
        return 0;
    }
}
