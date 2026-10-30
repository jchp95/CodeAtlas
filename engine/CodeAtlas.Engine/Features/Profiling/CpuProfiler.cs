using System.Text.Json;

sealed record CpuOptions(ProfileTarget Target, int DurationSeconds, int Repeat, int TimeoutSeconds, double HotThreshold);

/// <summary>
/// CPU profile through dotnet-trace (EventPipe sampling). Reports, per method, the CPU time it spent itself or in framework code it called
/// ("own"), exclusive and inclusive CPU, and blocked time, apart. Repeated runs give a range so noise can be told from change.
/// </summary>
static class CpuProfiler
{
    public static CpuProfileReport Run(string root, GraphIndex? index, CpuOptions options, CancellationToken cancellation)
    {
        var tool = ProfilingTools.Find("dotnet-trace");
        var limitations = new List<string>
        {
            "Sampling (about one sample per millisecond per running thread): short methods can be missed, and call counts cannot be measured.",
            "The EventPipe sampler stops the runtime at safe points to read stacks, so a small method that contains safe points (for example in a loop) can be charged some time that belongs to a neighbour (safe-point bias).",
            "Inlined methods are attributed to their caller; the first seconds include JIT warm-up (tiered compilation).",
            "Times are from this machine and this run; they say where time went, not whether that time is wasted."
        };
        var notes = new List<string>();
        if (tool is null) return Failed(root, options, "toolMissing", limitations, new[] { "dotnet-trace is not installed. " + ProfilingTools.InstallHint });

        var directory = ProfilingTools.PrepareOutputDirectory(root, "cpu");
        var commands = new List<string>();
        var runs = new List<CpuRun>();
        var aggregates = new List<SpeedscopeCpu.Aggregate>();
        var resolutionCache = new Dictionary<string, (GraphNode? Node, string Resolution)>(StringComparer.Ordinal);

        (GraphNode? Node, string Resolution) Resolve(string frame)
        {
            if (resolutionCache.TryGetValue(frame, out var cached)) return cached;
            var (module, symbol) = SpeedscopeCpu.Split(frame);
            var result = index is null || module.Length == 0 || symbol.Length == 0 ? (null, "notResolved") : StackTraceResolver.ResolveSymbol(index, symbol);
            return resolutionCache[frame] = result;
        }

        for (var i = 1; i <= options.Repeat; i++)
        {
            cancellation.ThrowIfCancellationRequested();
            var output = Path.Combine(directory, $"run{i}");
            var arguments = new List<string> { "collect", "--profile", "dotnet-sampled-thread-time", "--format", "Speedscope", "--output", output };
            if (options.DurationSeconds > 0 || options.Target.AttachProcessId is not null)
            {
                arguments.Add("--duration"); arguments.Add(TimeSpan.FromSeconds(Math.Max(options.DurationSeconds, 1)).ToString(@"dd\:hh\:mm\:ss"));
            }
            if (options.Target.AttachProcessId is { } pid) { arguments.Add("-p"); arguments.Add(pid.ToString()); }
            else { arguments.Add("--"); arguments.Add("dotnet"); arguments.Add(options.Target.Program!); arguments.AddRange(options.Target.Arguments); }
            commands.Add("dotnet-trace " + string.Join(' ', arguments));

            var outcome = BoundedProcess.Run(tool, arguments, directory, TimeSpan.FromSeconds(options.TimeoutSeconds), cancellation, 15);
            var file = output + ".speedscope.json";
            if (outcome.Cancelled) { runs.Add(new CpuRun(i, file, 0, 0, 0, 0, 0, "cancelled")); break; }
            if (outcome.TimedOut || !File.Exists(file))
            {
                runs.Add(new CpuRun(i, file, 0, 0, 0, 0, 0, outcome.TimedOut ? "timedOut" : "noTrace"));
                notes.AddRange(outcome.Tail.TakeLast(5));
                continue;
            }
            if (new FileInfo(file).Length > 400L * 1024 * 1024) { runs.Add(new CpuRun(i, file, 0, 0, 0, 0, 0, "oversized")); notes.Add($"Run {i}: the trace exceeds 400 MB and was not parsed."); continue; }

            var aggregate = SpeedscopeCpu.Parse(file, frame => Resolve(frame).Node is not null);
            aggregates.Add(aggregate);
            runs.Add(new CpuRun(i, file, Math.Round(aggregate.SpanMs, 1), Math.Round(aggregate.CpuMs, 1), Math.Round(aggregate.BlockedMs, 1), aggregate.Threads, Math.Round(aggregate.StepMs, 3), "completed"));
        }

        var status = runs.Any(r => r.Status == "cancelled") ? "cancelled" : aggregates.Count == 0 ? "traceFailed" : aggregates.Count < runs.Count ? "partial" : "completed";
        if (aggregates.Count == 0) return new CpuProfileReport(1, "cpu", root, DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss zzz"), status, options.Target, commands.ToArray(), runs.ToArray(),
            Array.Empty<CpuMethod>(), null, Array.Empty<Finding>(), limitations.ToArray(), notes.ToArray());

        var staticFindings = index is null ? Array.Empty<Finding>() : new CodeAnalysisEngine(root, new AnalysisOptions()).Run(CancellationToken.None).Findings;
        var methods = Summarize(aggregates, Resolve, index, staticFindings);
        var median = aggregates.OrderBy(a => a.CpuMs).ElementAt((aggregates.Count - 1) / 2);
        var tree = BuildTree(median.Root, median.CpuMs, Resolve);
        var findings = HotSpotFindings(root, methods, runs.Where(r => r.Status == "completed").ToArray(), commands, options, index);
        if (aggregates.Count == 1) notes.Add("A single run cannot separate noise from change: use --repeat 3 or more before comparing profiles.");
        if (runs.Any(r => r.CpuMs < 500)) notes.Add("Fewer than 500 ms of CPU were sampled in some run; the percentages are weak evidence.");
        return new CpuProfileReport(1, "cpu", root, DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss zzz"), status, options.Target, commands.ToArray(), runs.ToArray(), methods, tree, findings,
            limitations.ToArray(), notes.ToArray());
    }

    private static CpuMethod[] Summarize(List<SpeedscopeCpu.Aggregate> runs, Func<string, (GraphNode? Node, string Resolution)> resolve, GraphIndex? index, Finding[] staticFindings)
    {
        double Median(IEnumerable<double> values) { var a = values.OrderBy(v => v).ToArray(); return a.Length == 0 ? 0 : a.Length % 2 == 1 ? a[a.Length / 2] : (a[a.Length / 2 - 1] + a[a.Length / 2]) / 2; }
        var all = runs.SelectMany(r => r.Methods.Keys).Distinct(StringComparer.Ordinal).ToArray();
        var rows = new List<CpuMethod>();
        foreach (var frame in all)
        {
            double Share(SpeedscopeCpu.Aggregate r, Func<SpeedscopeCpu.MethodAccumulator, double> f) => r.Methods.TryGetValue(frame, out var m) && r.CpuMs > 0 ? f(m) / r.CpuMs : 0;
            var own = runs.Select(r => Share(r, m => m.Own)).ToArray();
            var incl = runs.Select(r => Share(r, m => m.Inclusive)).ToArray();
            var (node, resolution) = resolve(frame);
            var (_, symbol) = SpeedscopeCpu.Split(frame);
            var ids = node is null ? Array.Empty<string>() : staticFindings.Where(f => f.Location.Symbol == $"{node.ContainingType}.{node.Signature}").Select(f => f.Id).ToArray();
            rows.Add(new CpuMethod(symbol, node?.Id, resolution, node?.Location,
                Math.Round(Median(runs.Select(r => r.Methods.TryGetValue(frame, out var m) ? m.Own : 0)), 1), Math.Round(Median(own), 4),
                Math.Round(Median(runs.Select(r => r.Methods.TryGetValue(frame, out var m) ? m.Exclusive : 0)), 1),
                Math.Round(Median(runs.Select(r => r.Methods.TryGetValue(frame, out var m) ? m.Inclusive : 0)), 1), Math.Round(Median(incl), 4),
                Math.Round(Median(runs.Select(r => r.Methods.TryGetValue(frame, out var m) ? m.Blocked : 0)), 1),
                Math.Round(own.Min(), 4), Math.Round(own.Max(), 4), runs.Count(r => r.Methods.ContainsKey(frame)), ids));
        }
        // Source methods always, plus the biggest framework methods, so the list stays bounded but nothing from the solution is hidden.
        return rows.Where(r => r.NodeId is not null).Concat(rows.Where(r => r.NodeId is null).OrderByDescending(r => r.ExclusiveCpuMs).Take(40))
            .OrderByDescending(r => r.OwnCpuMs).ThenByDescending(r => r.ExclusiveCpuMs).ThenBy(r => r.Symbol, StringComparer.Ordinal).Take(120).ToArray();
    }

    private static CallTreeNode BuildTree(SpeedscopeCpu.TrieNode root, double total, Func<string, (GraphNode? Node, string Resolution)> resolve)
    {
        CallTreeNode Convert(SpeedscopeCpu.TrieNode node, int depth)
        {
            var children = depth >= 14 ? Array.Empty<CallTreeNode>() : node.Children.Values.Where(c => total > 0 && c.Inclusive / total >= 0.01)
                .OrderByDescending(c => c.Inclusive).Take(8).Select(c => Convert(c, depth + 1)).ToArray();
            return new CallTreeNode(SpeedscopeCpu.Split(node.Symbol).Symbol, node.Symbol == "(root)" ? null : resolve(node.Symbol).Node?.Id, Math.Round(node.Inclusive, 1), total > 0 ? Math.Round(node.Inclusive / total, 4) : 0, children);
        }
        return Convert(root, 0);
    }

    private static Finding[] HotSpotFindings(string root, CpuMethod[] methods, CpuRun[] runs, List<string> commands, CpuOptions options, GraphIndex? index)
    {
        var findings = new List<Finding>();
        foreach (var method in methods.Where(m => m.NodeId is not null && m.OwnShare >= options.HotThreshold && index!.Nodes[m.NodeId!].Name != "Main"))
        {
            var node = index!.Nodes[method.NodeId!];
            var many = runs.Length >= 3;
            findings.Add(GraphFindings.Create("PRF0001", method.OwnShare >= 0.5 ? FindingSeverity.Warning : FindingSeverity.Info, ValidationLevel.Measured,
                $"{node.ContainingType}.{node.Signature} accounts for {method.OwnShare:P0} of sampled CPU",
                $"Across {runs.Length} run(s), this method — including the framework code it called — held the CPU for a median of {method.OwnCpuMs:N0} ms ({method.OwnShare:P1}; range {method.MinOwnShare:P1}–{method.MaxOwnShare:P1}).",
                GraphFindings.LocationOf(node),
                new[]
                {
                    new FindingEvidence("cpu-profile", $"own CPU {method.OwnCpuMs:N0} ms ({method.OwnShare:P1}), exclusive {method.ExclusiveCpuMs:N0} ms, inclusive {method.InclusiveCpuMs:N0} ms ({method.InclusiveShare:P1}); present in {method.RunsPresent}/{runs.Length} run(s)"),
                    new FindingEvidence("trace", string.Join("; ", runs.Select(r => r.TraceFile)))
                }.Concat(method.StaticFindingIds.Select(id => new FindingEvidence("static-finding", $"Static finding {id} sits on this method"))).ToArray(),
                null, many ? "high" : "medium",
                new[] { "Sampling: shares are estimates, with an error that shrinks with the number of samples.", "A hot method is where time goes; it is not necessarily slow or wrong.", many ? "" : "Single run: noise cannot be separated from signal." }.Where(x => x.Length > 0).ToArray(),
                "Changes to this method have the largest effect on CPU use in this scenario; changes elsewhere cannot move it much.",
                "Before changing anything, write a BenchmarkDotNet benchmark for this method with representative inputs, then compare alternatives with it. Do not accept an optimization because the code looks simpler.",
                commands.FirstOrDefault() ?? "", $"{node.Id}", FindingOrigin.Profile) with { RelatedFindingIds = method.StaticFindingIds });
        }
        return findings.ToArray();
    }

    private static CpuProfileReport Failed(string root, CpuOptions options, string status, List<string> limitations, string[] notes) =>
        new(1, "cpu", root, DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss zzz"), status, options.Target, Array.Empty<string>(), Array.Empty<CpuRun>(), Array.Empty<CpuMethod>(), null, Array.Empty<Finding>(), limitations.ToArray(), notes);
}
