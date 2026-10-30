using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

sealed record DebugLaunch(string? ProgramDll, string[] Arguments, string WorkingDirectory, int? AttachProcessId, int StopTimeoutSeconds);

/// <summary>
/// One debugging session over a real debug adapter (netcoredbg). Every command is answered with exactly one result; commands that resume the
/// debuggee wait for the next stop or exit and describe it: stack frames resolved against the call graph, locals, and exception details.
/// Nothing is simulated: if the adapter cannot do something, the command fails with the adapter's own message.
/// </summary>
sealed partial class DebugSession : IDisposable
{
    [GeneratedRegex(@"pass(word)?|pwd|secret|token|api[_-]?key|connection[_-]?string|credential|bearer", RegexOptions.IgnoreCase)]
    private static partial Regex SensitiveName();

    private readonly string root;
    private readonly DapClient client;
    private readonly GraphIndex? index;
    private readonly DebugLaunch launch;
    private Task<JsonNode>? launchResponse;
    private int currentThread;

    public DebugSession(string root, string adapterPath, GraphIndex? index, DebugLaunch launch)
    {
        this.root = root;
        this.index = index;
        this.launch = launch;
        client = new DapClient(adapterPath, new[] { "--interpreter=vscode" });
    }

    public JsonObject Start()
    {
        var capabilities = client.Request("initialize", new JsonObject
        {
            ["clientID"] = "codeatlas", ["clientName"] = "CodeAtlas", ["adapterID"] = "coreclr", ["pathFormat"] = "path",
            ["linesStartAt1"] = true, ["columnsStartAt1"] = true, ["supportsVariableType"] = true
        });
        var request = launch.AttachProcessId is { } pid
            ? new JsonObject { ["name"] = ".NET Attach", ["type"] = "coreclr", ["request"] = "attach", ["processId"] = pid }
            : new JsonObject
            {
                ["name"] = ".NET Launch", ["type"] = "coreclr", ["request"] = "launch", ["program"] = "dotnet",
                ["args"] = new JsonArray(new[] { launch.ProgramDll! }.Concat(launch.Arguments).Select(a => (JsonNode)a!).ToArray()),
                ["cwd"] = launch.WorkingDirectory, ["stopAtEntry"] = false
            };
        launchResponse = client.RequestAsync(launch.AttachProcessId is null ? "launch" : "attach", request);
        // The adapter announces readiness for breakpoints with 'initialized'.
        for (var waited = 0; waited < 20_000;)
        {
            var e = client.NextEvent(500);
            if (e is null) { waited += 500; if (client.HasExited) throw new InvalidOperationException("The debug adapter exited during startup."); continue; }
            if (e["event"]?.GetValue<string>() == "initialized") return new JsonObject { ["state"] = "configuring", ["supports"] = SelectCapabilities(capabilities) };
        }
        throw new TimeoutException("The debug adapter never reported 'initialized'.");
    }

    private static JsonObject SelectCapabilities(JsonNode caps) => new()
    {
        ["conditionalBreakpoints"] = caps["supportsConditionalBreakpoints"]?.GetValue<bool>() ?? false,
        ["hitConditionalBreakpoints"] = caps["supportsHitConditionalBreakpoints"]?.GetValue<bool>() ?? false,
        ["functionBreakpoints"] = caps["supportsFunctionBreakpoints"]?.GetValue<bool>() ?? false,
        ["logpoints"] = caps["supportsLogPoints"]?.GetValue<bool>() ?? false,
        ["exceptionInfo"] = caps["supportsExceptionInfoRequest"]?.GetValue<bool>() ?? false,
        ["exceptionFilters"] = caps["exceptionBreakpointFilters"]?.DeepClone() ?? new JsonArray(),
        ["evaluate"] = caps["supportsEvaluateForHovers"]?.GetValue<bool>() ?? false,
        ["asyncTaskInspection"] = false // not offered by DAP/netcoredbg: only async state-machine frames are visible
    };

    public JsonNode Handle(JsonObject command)
    {
        var name = command["cmd"]?.GetValue<string>() ?? "";
        switch (name)
        {
            case "breakpoints": return SetBreakpoints(command);
            case "functionBreakpoints": return SetFunctionBreakpoints(command);
            case "exceptions": return SetExceptions(command);
            case "run":
                client.Request("configurationDone", null);
                try { launchResponse?.Wait(15_000); } catch (AggregateException ex) { throw new InvalidOperationException("Launch failed: " + ex.InnerException?.Message); }
                return WaitForStop();
            case "continue": Resume("continue"); return WaitForStop();
            case "next": Resume("next"); return WaitForStop();
            case "stepIn": Resume("stepIn"); return WaitForStop();
            case "stepOut": Resume("stepOut"); return WaitForStop();
            case "pause":
                client.Request("pause", new JsonObject { ["threadId"] = command["threadId"]?.GetValue<int>() ?? DefaultThread() });
                return WaitForStop();
            case "stack": return new JsonObject { ["frames"] = Frames(command["threadId"]?.GetValue<int>() ?? currentThread, command["levels"]?.GetValue<int>() ?? 20) };
            case "variables": return new JsonObject { ["scopes"] = Scopes(command["frameId"]!.GetValue<int>(), command["depth"]?.GetValue<int>() ?? 1) };
            case "evaluate":
            {
                var body = client.Request("evaluate", new JsonObject { ["expression"] = command["expression"]!.GetValue<string>(), ["frameId"] = command["frameId"]?.GetValue<int>(), ["context"] = "repl" });
                var expression = command["expression"]!.GetValue<string>();
                return new JsonObject { ["expression"] = expression, ["result"] = SensitiveName().IsMatch(expression) ? "<redacted>" : body["result"]?.DeepClone(), ["type"] = body["type"]?.DeepClone() };
            }
            case "threads":
            {
                var body = client.Request("threads", null);
                return new JsonObject { ["threads"] = body["threads"]?.DeepClone() ?? new JsonArray() };
            }
            case "terminate": return Terminate();
            default: throw new InvalidOperationException($"Unknown command '{name}'.");
        }
    }

    private string Absolute(string file) => Path.IsPathRooted(file) ? file : Path.GetFullPath(Path.Combine(root, file));

    private JsonNode SetBreakpoints(JsonObject command)
    {
        var file = Absolute(command["file"]!.GetValue<string>());
        var items = command["items"] as JsonArray ?? new JsonArray((command["lines"] as JsonArray ?? new JsonArray()).Select(l => (JsonNode)new JsonObject { ["line"] = l!.GetValue<int>() }).ToArray());
        var body = client.Request("setBreakpoints", new JsonObject { ["source"] = new JsonObject { ["path"] = file }, ["breakpoints"] = items.DeepClone() });
        // A breakpoint the adapter could not bind is reported as such, with its message: never assumed to work.
        return new JsonObject { ["file"] = command["file"]!.GetValue<string>(), ["breakpoints"] = new JsonArray((body["breakpoints"] as JsonArray ?? new JsonArray()).Select(b => (JsonNode)new JsonObject
        {
            ["verified"] = b!["verified"]?.GetValue<bool>() ?? false, ["line"] = b["line"]?.DeepClone(), ["message"] = b["message"]?.DeepClone()
        }).ToArray()) };
    }

    private JsonNode SetFunctionBreakpoints(JsonObject command)
    {
        var names = (command["names"] as JsonArray ?? new JsonArray()).Select(n => (JsonNode)new JsonObject { ["name"] = n!.GetValue<string>() }).ToArray();
        var body = client.Request("setFunctionBreakpoints", new JsonObject { ["breakpoints"] = new JsonArray(names) });
        return new JsonObject { ["breakpoints"] = new JsonArray((body["breakpoints"] as JsonArray ?? new JsonArray()).Select(b => (JsonNode)new JsonObject
        {
            ["verified"] = b!["verified"]?.GetValue<bool>() ?? false, ["message"] = b["message"]?.DeepClone()
        }).ToArray()) };
    }

    /// <summary>filters: "all" (first chance) and/or "user-unhandled"; types: optional per-exception-type break modes (always|unhandled|never).</summary>
    private JsonNode SetExceptions(JsonObject command)
    {
        var arguments = new JsonObject { ["filters"] = command["filters"]?.DeepClone() ?? new JsonArray() };
        if (command["types"] is JsonArray types && types.Count > 0)
            arguments["exceptionOptions"] = new JsonArray(types.Select(t => (JsonNode)new JsonObject
            {
                ["path"] = new JsonArray(new JsonObject { ["names"] = new JsonArray(t!["type"]!.GetValue<string>()) }),
                ["breakMode"] = t["mode"]?.GetValue<string>() ?? "always"
            }).ToArray());
        var body = client.Request("setExceptionBreakpoints", arguments);
        return new JsonObject { ["filters"] = arguments["filters"]!.DeepClone(), ["adapterResponse"] = body.DeepClone() };
    }

    private void Resume(string request) =>
        client.Request(request, new JsonObject { ["threadId"] = DefaultThread() });

    /// <summary>The thread that last stopped; before any stop (e.g. right after an attach) the first thread the adapter lists.</summary>
    private int DefaultThread()
    {
        if (currentThread > 0) return currentThread;
        var threads = client.Request("threads", null)["threads"] as JsonArray;
        return threads?.FirstOrDefault()?["id"]?.GetValue<int>() ?? 1;
    }

    private JsonObject WaitForStop()
    {
        var output = new List<string>();
        var breakpointEvents = new JsonArray();
        int? exitCode = null;
        var deadline = DateTime.UtcNow.AddSeconds(launch.StopTimeoutSeconds);
        while (true)
        {
            var remaining = (int)(deadline - DateTime.UtcNow).TotalMilliseconds;
            if (remaining <= 0) return new JsonObject { ["state"] = "running", ["note"] = $"No stop within {launch.StopTimeoutSeconds} s; use 'pause' or 'terminate'.", ["output"] = Lines(output) };
            var e = client.NextEvent(Math.Min(remaining, 1000));
            if (e is null) { if (client.HasExited) return new JsonObject { ["state"] = "adapterExited", ["output"] = Lines(output) }; continue; }
            var body = e["body"];
            switch (e["event"]?.GetValue<string>())
            {
                case "output":
                    if (body?["category"]?.GetValue<string>() is "stdout" or "stderr" or "console" or null && body?["output"]?.GetValue<string>() is { } text && output.Count < 200)
                        output.Add(text.TrimEnd('\r', '\n'));
                    break;
                case "breakpoint":
                {
                    var changed = body?["breakpoint"];
                    if (changed is not null)
                    {
                        var entry = new JsonObject();
                        entry["reason"] = body!["reason"]?.DeepClone();
                        entry["verified"] = changed["verified"]?.DeepClone();
                        entry["line"] = changed["line"]?.DeepClone();
                        entry["message"] = changed["message"]?.DeepClone();
                        breakpointEvents.Add(entry);
                    }
                    break;
                }
                case "exited": exitCode = body?["exitCode"]?.GetValue<int>(); break;
                case "terminated": return new JsonObject { ["state"] = "exited", ["exitCode"] = exitCode, ["output"] = Lines(output) };
                case "stopped": { var stop = DescribeStop(body!, output); stop["breakpointEvents"] = breakpointEvents; return stop; }
            }
        }
    }

    private JsonObject DescribeStop(JsonNode body, List<string> output)
    {
        currentThread = body["threadId"]?.GetValue<int>() ?? currentThread;
        var reason = body["reason"]?.GetValue<string>() ?? "unknown";
        var frames = Frames(currentThread, 20);
        var result = new JsonObject { ["state"] = "stopped", ["reason"] = reason, ["description"] = body["description"]?.DeepClone(), ["threadId"] = currentThread, ["frames"] = frames, ["output"] = Lines(output) };
        if (frames.Count > 0 && frames[0]!["id"] is { } top) result["locals"] = Scopes(top.GetValue<int>(), 1);
        if (reason == "exception") result["exception"] = ExceptionDetails(currentThread);
        return result;
    }

    private JsonNode ExceptionDetails(int threadId)
    {
        try
        {
            var info = client.Request("exceptionInfo", new JsonObject { ["threadId"] = threadId });
            var details = new JsonObject
            {
                ["exceptionId"] = info["exceptionId"]?.DeepClone(), ["description"] = info["description"]?.DeepClone(), ["breakMode"] = info["breakMode"]?.DeepClone(),
                ["typeName"] = info["details"]?["typeName"]?.DeepClone(), ["message"] = info["details"]?["message"]?.DeepClone(), ["stackTrace"] = info["details"]?["stackTrace"]?.DeepClone()
            };
            // The stack trace the runtime captured goes through the same resolver as pasted traces: inner exceptions, origin, tests, entry points.
            if (index is not null && info["details"]?["stackTrace"]?.GetValue<string>() is { Length: > 0 } stack)
            {
                var header = $"{info["details"]?["typeName"]?.GetValue<string>() ?? info["exceptionId"]?.GetValue<string>() ?? "System.Exception"}: {info["details"]?["message"]?.GetValue<string>()}";
                var analysis = ExceptionAnalyzer.Analyze(root, index, header + "\n" + stack, new GraphLimits());
                details["analysis"] = System.Text.Json.JsonSerializer.SerializeToNode(analysis, JsonDefaults.Options);
            }
            return details;
        }
        catch (Exception ex) { return new JsonObject { ["error"] = ex.Message }; }
    }

    private JsonArray Frames(int threadId, int levels)
    {
        if (threadId <= 0) return new JsonArray();
        var body = client.Request("stackTrace", new JsonObject { ["threadId"] = threadId, ["startFrame"] = 0, ["levels"] = levels });
        var frames = new JsonArray();
        foreach (var frame in body["stackFrames"] as JsonArray ?? new JsonArray())
        {
            var path = frame!["source"]?["path"]?.GetValue<string>();
            var line = frame["line"]?.GetValue<int>();
            var relative = path is null ? null : Path.GetRelativePath(root, path).Replace('\\', '/');
            if (relative is not null && relative.StartsWith("../", StringComparison.Ordinal)) relative = path; // outside the repository (framework, packages)
            string? nodeId = null; var resolution = "external";
            if (index is not null && relative is not null && line is not null && path is not null && path.StartsWith(root, StringComparison.Ordinal))
            {
                var selection = SymbolSelector.Resolve(index, $"{relative}:{line}");
                nodeId = selection.Status == SelectorStatus.Found ? selection.Matches[0].Id : null;
                resolution = selection.Status == SelectorStatus.Found ? "resolved" : selection.Status == SelectorStatus.Ambiguous ? "ambiguous" : "notInGraph";
            }
            frames.Add(new JsonObject { ["id"] = frame["id"]?.DeepClone(), ["name"] = frame["name"]?.DeepClone(), ["file"] = relative, ["line"] = line, ["nodeId"] = nodeId, ["resolution"] = resolution });
        }
        return frames;
    }

    private JsonArray Scopes(int frameId, int depth)
    {
        var result = new JsonArray();
        var scopes = client.Request("scopes", new JsonObject { ["frameId"] = frameId });
        foreach (var scope in scopes["scopes"] as JsonArray ?? new JsonArray())
        {
            var reference = scope!["variablesReference"]?.GetValue<int>() ?? 0;
            result.Add(new JsonObject { ["name"] = scope["name"]?.DeepClone(), ["variables"] = Variables(reference, depth, 40) });
        }
        return result;
    }

    private JsonArray Variables(int reference, int depth, int limit)
    {
        var list = new JsonArray();
        if (reference <= 0) return list;
        var body = client.Request("variables", new JsonObject { ["variablesReference"] = reference });
        var all = body["variables"] as JsonArray ?? new JsonArray();
        foreach (var variable in all.Take(limit))
        {
            var name = variable!["name"]?.GetValue<string>() ?? "";
            var sensitive = SensitiveName().IsMatch(name);
            var entry = new JsonObject { ["name"] = name, ["value"] = sensitive ? "<redacted>" : variable["value"]?.DeepClone(), ["type"] = variable["type"]?.DeepClone(), ["redacted"] = sensitive };
            var child = variable["variablesReference"]?.GetValue<int>() ?? 0;
            if (!sensitive && child > 0 && depth > 1) entry["children"] = Variables(child, depth - 1, 20);
            list.Add(entry);
        }
        if (all.Count > limit) list.Add(new JsonObject { ["name"] = "…", ["value"] = $"{all.Count - limit} more not shown" });
        return list;
    }

    private JsonNode Terminate()
    {
        // An attached process is detached, never killed.
        try { client.Request("disconnect", new JsonObject { ["terminateDebuggee"] = launch.AttachProcessId is null }, 5000); }
        catch { /* the adapter may close first */ }
        return new JsonObject { ["state"] = "terminated" };
    }

    private static JsonArray Lines(List<string> lines) => new(lines.Select(l => (JsonNode)(l.Length > 400 ? l[..400] + "…" : l)).ToArray());

    public void Dispose() => client.Dispose();
}
