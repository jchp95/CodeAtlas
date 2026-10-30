using System.Globalization;
using System.Text.Json;

sealed record CounterOptions(ProfileTarget Target, int DurationSeconds, int RefreshSeconds, int TimeoutSeconds);

/// <summary>
/// Runtime metrics through dotnet-counters (System.Runtime meter). Separates allocation (rate) from retention (post-GC heap size), reports GC
/// pauses, thread-pool pressure and lock contention, and states how many samples each conclusion rests on.
/// </summary>
static class CounterProfiler
{
    private const double MiB = 1024 * 1024;

    public static CounterProfileReport Run(string root, GraphIndex? index, CounterOptions options, CancellationToken cancellation)
    {
        var limitations = new[]
        {
            "System.Runtime counters are sampled once per refresh interval; short spikes between samples are not seen.",
            "Allocation rate shows how much memory is created, not how much is kept: only the post-GC heap size speaks about retention, and only a heap comparison names what is retained.",
            "A trend over fewer than 8 samples is not reported as a finding."
        };
        var tool = ProfilingTools.Find("dotnet-counters");
        if (tool is null) return Empty(root, options, "toolMissing", "", limitations, new[] { "dotnet-counters is not installed. " + ProfilingTools.InstallHint });

        var directory = ProfilingTools.PrepareOutputDirectory(root, "counters");
        var output = Path.Combine(directory, "counters");
        var arguments = new List<string> { "collect", "--format", "json", "--refresh-interval", options.RefreshSeconds.ToString(CultureInfo.InvariantCulture), "--counters", "System.Runtime", "--output", output };
        if (options.Target.AttachProcessId is { } pid)
        {
            arguments.Add("-p"); arguments.Add(pid.ToString());
            arguments.Add("--duration"); arguments.Add(TimeSpan.FromSeconds(Math.Max(options.DurationSeconds, 2)).ToString(@"dd\:hh\:mm\:ss"));
        }
        else
        {
            arguments.Add("--"); arguments.Add("dotnet"); arguments.Add(options.Target.Program!); arguments.AddRange(options.Target.Arguments);
        }
        var command = "dotnet-counters " + string.Join(' ', arguments);
        var outcome = BoundedProcess.Run(tool, arguments, directory, TimeSpan.FromSeconds(options.TimeoutSeconds), cancellation, 15);
        var file = output + ".json";
        if (outcome.Cancelled) return Empty(root, options, "cancelled", command, limitations, Array.Empty<string>());
        if (outcome.TimedOut || !File.Exists(file)) return Empty(root, options, outcome.TimedOut ? "timedOut" : "noData", command, limitations, outcome.Tail);

        var events = JsonDocument.Parse(File.ReadAllBytes(file)).RootElement.GetProperty("Events").EnumerateArray().Select(e => (
            Time: DateTimeOffset.Parse(e.GetProperty("timestamp").GetString()!, CultureInfo.InvariantCulture),
            Name: e.GetProperty("name").GetString()!.Split(" (")[0], Tags: e.GetProperty("tags").GetString() ?? "", Value: e.GetProperty("value").GetDouble())).ToArray();
        if (events.Length == 0) return Empty(root, options, "noData", command, limitations, new[] { "The counter file has no samples." });

        var start = events.Min(e => e.Time);
        // The first sample of a rate covers a partial interval; it is dropped from the series.
        double[] Seconds(IEnumerable<DateTimeOffset> t) => t.Select(x => (x - start).TotalSeconds).ToArray();
        List<(double T, double V)> Sum(string name, bool dropFirst = true)
        {
            var points = events.Where(e => e.Name == name).GroupBy(e => (long)Math.Round((e.Time - start).TotalSeconds / options.RefreshSeconds)).OrderBy(g => g.Key)
                .Select(g => (T: g.Key * (double)options.RefreshSeconds, V: g.Sum(x => x.Value))).ToList();
            if (dropFirst && points.Count > 1) points.RemoveAt(0);
            return points;
        }

        var heap = Sum("dotnet.gc.last_collection.heap.size", false);
        var allocation = Sum("dotnet.gc.heap.total_allocated");
        var pause = Sum("dotnet.gc.pause.time");
        var collections = Sum("dotnet.gc.collections");
        var cpu = Sum("dotnet.process.cpu.time");
        var workingSet = Sum("dotnet.process.memory.working_set", false);
        // These two are up/down counters: each sample is the CHANGE during the interval, so the level is the running sum.
        // The queue counter reports changes and its starting level is unknown (the running sum can go negative), so the level is expressed
        // relative to the emptiest moment observed: it is a lower bound of the real queue length, with a correct amplitude.
        var queue = Rebase(Cumulative(Sum("dotnet.thread_pool.queue.length", true), options.RefreshSeconds));
        var threads = Cumulative(Sum("dotnet.thread_pool.thread.count", false), options.RefreshSeconds);
        var contentions = Sum("dotnet.monitor.lock_contentions");
        var duration = events.Max(e => e.Time).Subtract(start).TotalSeconds;

        var series = new List<SeriesSummary>();
        void Add(string name, string unit, List<(double T, double V)> points, double scale = 1) { if (points.Count > 0) series.Add(Summarize(name, unit, points, scale)); }
        Add("heapAfterGc", "MiB", heap, 1 / MiB);
        Add("allocationRate", "MiB/s", allocation, 1 / MiB);
        Add("workingSet", "MiB", workingSet, 1 / MiB);
        Add("gcPauseFraction", "fraction of time", pause);
        Add("cpuCores", "cores", cpu);
        Add("threadPoolQueueLength", "items", queue);
        Add("threadPoolThreads", "threads", threads);
        Add("lockContentionsPerSecond", "1/s", contentions);

        var totals = new Dictionary<string, double>
        {
            ["allocatedMiB"] = Math.Round(allocation.Sum(p => p.V) * options.RefreshSeconds / MiB, 1),
            ["gcCollections"] = Math.Round(collections.Sum(p => p.V) * options.RefreshSeconds, 0),
            ["gcPauseSeconds"] = Math.Round(pause.Sum(p => p.V) * options.RefreshSeconds, 3),
            ["lockContentions"] = Math.Round(contentions.Sum(p => p.V) * options.RefreshSeconds, 0)
        };

        var findings = Analyze(root, index, options, command, series, totals, duration);
        var notes = new List<string>();
        if (series.FirstOrDefault(s => s.Name == "heapAfterGc") is { Samples: < 8 }) notes.Add($"Fewer than 8 post-GC heap samples ({heap.Count}): run longer or use a shorter --refresh-seconds before trusting trends.");
        return new CounterProfileReport(1, "counters", root, DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss zzz"), "completed", options.Target, command, Math.Round(duration, 1), options.RefreshSeconds,
            series.ToArray(), totals, findings, limitations, notes.ToArray());
    }

    private static List<(double T, double V)> Rebase(List<(double T, double V)> points)
    {
        if (points.Count == 0) return points;
        var floor = points.Min(p => p.V);
        return points.Select(p => (p.T, p.V - floor)).ToList();
    }

    private static List<(double T, double V)> Cumulative(List<(double T, double V)> deltas, int refresh)
    {
        double level = 0;
        return deltas.Select(p => (p.T, V: level += p.V * refresh)).ToList();
    }

    private static SeriesSummary Summarize(string name, string unit, List<(double T, double V)> points, double scale)
    {
        var values = points.Select(p => p.V * scale).ToArray();
        var sorted = values.OrderBy(v => v).ToArray();
        var n = values.Length;
        // Least-squares slope and r² against time.
        double slope = 0, r2 = 0;
        if (n >= 3)
        {
            var xs = points.Select(p => p.T).ToArray();
            double mx = xs.Average(), my = values.Average();
            double sxx = xs.Sum(x => (x - mx) * (x - mx)), sxy = xs.Zip(values, (x, y) => (x - mx) * (y - my)).Sum(), syy = values.Sum(y => (y - my) * (y - my));
            slope = sxx > 0 ? sxy / sxx : 0;
            r2 = sxx > 0 && syy > 0 ? sxy * sxy / (sxx * syy) : 0;
        }
        return new SeriesSummary(name, unit, n, Math.Round(sorted[0], 3), Math.Round(values.Average(), 3), Math.Round(sorted[Math.Min(n - 1, (int)Math.Ceiling(n * 0.95) - 1)], 3),
            Math.Round(sorted[^1], 3), Math.Round(values[^1], 3), Math.Round(slope, 4), Math.Round(r2, 3),
            n < 2 ? 0 : Math.Round(Enumerable.Range(1, n - 1).Count(i => values[i] >= values[i - 1] * 0.98) / (double)(n - 1), 3));
    }

    private static Finding[] Analyze(string root, GraphIndex? index, CounterOptions options, string command, List<SeriesSummary> series, Dictionary<string, double> totals, double duration)
    {
        SeriesSummary? Get(string n) => series.FirstOrDefault(s => s.Name == n);
        var findings = new List<Finding>();
        var location = TargetLocation(index, options.Target);
        Finding Make(string rule, FindingSeverity severity, string title, string description, FindingEvidence[] evidence, string impact, string recommendation, string[] limits, string[]? related = null) =>
            GraphFindings.Create(rule, severity, ValidationLevel.Measured, title, description, location, evidence, null, "medium", limits, impact, recommendation, command, rule + "|" + (options.Target.Program ?? options.Target.AttachProcessId?.ToString()), FindingOrigin.Profile) with { RelatedFindingIds = related };

        var heap = Get("heapAfterGc");
        var allocation = Get("allocationRate");
        // Sustained growth: almost every sample at or above the previous one, clearly higher at the end, and not a few MiB of noise.
        if (heap is { Samples: >= 8 } && heap.RisingFraction >= 0.8 && heap.SlopePerSecond > 0 && heap.Last >= heap.Min * 1.2 && heap.Last - heap.Min >= 8)
            findings.Add(Make("PRF0101", FindingSeverity.Warning, $"Post-GC heap grew steadily to {heap.Last:N0} MiB",
                $"Over {duration:N0} s the heap measured after each garbage collection rose at about {heap.SlopePerSecond:N1} MiB/s (non-decreasing in {heap.RisingFraction:P0} of {heap.Samples - 1} intervals), from {heap.Min:N0} to {heap.Last:N0} MiB.",
                new[] { new FindingEvidence("counters", $"dotnet.gc.last_collection.heap.size: min {heap.Min:N1}, last {heap.Last:N1} MiB, slope {heap.SlopePerSecond:N2} MiB/s, non-decreasing in {heap.RisingFraction:P0} of intervals") },
                "Memory that survives collections keeps growing: this is the signature of retention. If it continues, the process will exhaust memory or spend its time collecting.",
                "Take two heap snapshots with 'profile heap' a few seconds apart and compare which types grow, then find what keeps them reachable. Growth during a warm-up or a cache filling up looks the same, so repeat after the process is warm.",
                new[] { "Consistent with a leak but not proof of one: caches, buffers and warm-up also grow.", "The counter does not say which objects are retained." }));
        if (allocation is { Samples: >= 3 } && allocation.Mean >= 50)
        {
            var flat = heap is { Samples: >= 8 } && (heap.SlopePerSecond <= 0.5 || heap.RSquared < 0.5);
            findings.Add(Make("PRF0102", FindingSeverity.Info, $"Allocation rate averages {allocation.Mean:N0} MiB/s",
                $"The process allocated {totals["allocatedMiB"]:N0} MiB in {duration:N0} s (mean {allocation.Mean:N0}, max {allocation.Max:N0} MiB/s). " + (flat ? "The post-GC heap stayed flat, so this is churn (short-lived garbage), not retention." : "Retention was not ruled out: see the post-GC heap series."),
                new[] { new FindingEvidence("counters", $"dotnet.gc.heap.total_allocated mean {allocation.Mean:N1} MiB/s over {allocation.Samples} samples") },
                "A high allocation rate means frequent collections and CPU spent in the allocator and GC; it does not by itself mean a leak.",
                "Find what allocates with a CPU/allocation profile of the hot path; reuse buffers (ArrayPool), avoid per-call allocations in loops, and re-measure the rate.",
                new[] { "Allocation is not retention.", "The counter does not say which code allocates." }));
        }
        var pause = Get("gcPauseFraction");
        if (pause is { Samples: >= 3 } && totals["gcPauseSeconds"] / Math.Max(duration, 1) >= 0.05)
            findings.Add(Make("PRF0103", FindingSeverity.Warning, $"{totals["gcPauseSeconds"] / Math.Max(duration, 1):P1} of the run was spent paused for GC",
                $"GC pauses added up to {totals["gcPauseSeconds"]:N2} s over {duration:N0} s with {totals["gcCollections"]:N0} collections.",
                new[] { new FindingEvidence("counters", $"dotnet.gc.pause.time total {totals["gcPauseSeconds"]:N3} s; collections {totals["gcCollections"]:N0}") },
                "Threads are stopped during those pauses; latency spikes and lower throughput follow.", "Reduce allocation or the size of the surviving heap; consider a different GC mode only after measuring.",
                new[] { "Pause time is summed from per-interval rates." }));
        var queue = Get("threadPoolQueueLength");
        var poolThreads = Get("threadPoolThreads");
        if (queue is { Samples: >= 5 } && queue.Max >= 5 && poolThreads is not null && poolThreads.SlopePerSecond > 0 && poolThreads.Max >= 2 * Environment.ProcessorCount)
        {
            var related = index is null ? Array.Empty<string>() : new CodeAnalysisEngine(root, new AnalysisOptions(true, new[] { "ATL1001" })).Run(CancellationToken.None).Findings.Select(f => f.Id).ToArray();
            findings.Add(Make("PRF0201", FindingSeverity.Warning, $"Thread-pool queue reached {queue.Max:N0} items while threads grew to {poolThreads.Max:N0}",
                $"The thread-pool queue (relative to its emptiest moment) reached {queue.Max:N0} items; the pool had up to {poolThreads.Max:N0} threads ({Environment.ProcessorCount} cores). Work waits while the pool injects threads slowly.",
                new[] { new FindingEvidence("counters", $"relative queue max {queue.Max:N0} (p95 {queue.P95:N0}); threads max {poolThreads.Max:N0}, slope {poolThreads.SlopePerSecond:N2}/s") }
                    .Concat(related.Select(id => new FindingEvidence("static-finding", $"Static blocking-on-async finding {id} is a candidate cause"))).ToArray(),
                "Requests queue behind blocked threads: latency grows and the service can appear hung (thread-pool starvation).",
                "Look for threads blocked on asynchronous work (.Result/.Wait()/GetResult) and for long synchronous work on pool threads; make them asynchronous. Re-measure the queue length afterwards.",
                new[] { "The counters show the symptom, not its cause; the static findings listed are candidates, not confirmed causes." }, related));
        }
        var locks = Get("lockContentionsPerSecond");
        if (locks is { Samples: >= 3 } && locks.Mean >= 10 && totals["lockContentions"] >= 100)
            findings.Add(Make("PRF0202", FindingSeverity.Warning, $"Lock contention averages {locks.Mean:N0}/s ({totals["lockContentions"]:N0} total)",
                $"Threads found a Monitor lock already held {totals["lockContentions"]:N0} times in {duration:N0} s.",
                new[] { new FindingEvidence("counters", $"dotnet.monitor.lock_contentions mean {locks.Mean:N1}/s, max {locks.Max:N0}/s over {locks.Samples} samples") },
                "Threads wait for each other instead of working; throughput does not scale with more threads.",
                "Shorten the critical section, shard the state, or use concurrent collections; re-measure the contention rate.",
                new[] { "Which lock is contended is not in the counter: take a CPU/wait profile or dump stacks to find it." }));
        return findings.ToArray();
    }

    internal static FindingLocation TargetLocation(GraphIndex? index, ProfileTarget target)
    {
        var name = target.Program is null ? null : Path.GetFileNameWithoutExtension(target.Program);
        if (index is not null && name is not null && index.Graph.ProjectFiles.TryGetValue(name, out var file)) return new FindingLocation(file, 1, 1, 1, null, name);
        return new FindingLocation(target.Program ?? $"pid:{target.AttachProcessId}", 1, 1, 1, null, name);
    }

    private static CounterProfileReport Empty(string root, CounterOptions options, string status, string command, string[] limitations, string[] notes) =>
        new(1, "counters", root, DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss zzz"), status, options.Target, command, 0, options.RefreshSeconds, Array.Empty<SeriesSummary>(), new Dictionary<string, double>(),
            Array.Empty<Finding>(), limitations, notes);
}
