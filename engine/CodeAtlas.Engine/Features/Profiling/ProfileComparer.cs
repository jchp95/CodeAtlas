using System.Text.Json;
using System.Text.Json.Nodes;

/// <summary>
/// Compares two reports of the same kind. A change is called a regression or improvement only when it is large and the repeated runs
/// of the two sides do not overlap; otherwise it is "noSignificantChange" or "inconclusive" (single run, or overlapping ranges).
/// </summary>
static class ProfileComparer
{
    private const double RelativeThreshold = 0.25;

    public static ProfileComparison Compare(string baselinePath, string currentPath)
    {
        var a = JsonNode.Parse(File.ReadAllText(baselinePath))!;
        var b = JsonNode.Parse(File.ReadAllText(currentPath))!;
        var kind = a["kind"]?.GetValue<string>();
        if (kind is null || kind != b["kind"]?.GetValue<string>()) throw new InvalidOperationException("The two reports are not of the same kind (cpu, counters or heap).");
        var deltas = kind switch { "cpu" => Cpu(a, b), "counters" => Counters(a, b), "heap" => Heap(a, b), _ => throw new InvalidOperationException($"Unknown report kind '{kind}'.") };
        var verdict = deltas.Any(d => d.Verdict == "regression") ? "regression" : deltas.Any(d => d.Verdict == "improvement") ? "improvement" : deltas.Any(d => d.Verdict == "inconclusive") ? "inconclusive" : "noSignificantChange";
        return new ProfileComparison(1, kind, baselinePath, currentPath, verdict, deltas.ToArray(), new[]
        {
            "Measurements come from real runs on one machine. 'regression'/'improvement' require a change of at least 25 % whose repeated-run ranges do not overlap on both sides.",
            "'inconclusive' means a change was seen but the evidence (a single run, or overlapping ranges) cannot separate it from noise: repeat the runs before concluding.",
            "A simpler-looking or shorter method is not evidence of an improvement; only measurements are."
        });
    }

    private static List<ProfileDelta> Cpu(JsonNode a, JsonNode b)
    {
        var runsA = a["runs"]!.AsArray().Count(r => r!["status"]!.GetValue<string>() == "completed");
        var runsB = b["runs"]!.AsArray().Count(r => r!["status"]!.GetValue<string>() == "completed");
        var methodsA = a["methods"]!.AsArray().Where(m => m!["nodeId"] is not null).ToDictionary(m => m!["symbol"]!.GetValue<string>());
        var methodsB = b["methods"]!.AsArray().Where(m => m!["nodeId"] is not null).ToDictionary(m => m!["symbol"]!.GetValue<string>());
        var deltas = new List<ProfileDelta>();
        foreach (var symbol in methodsA.Keys.Union(methodsB.Keys).OrderBy(s => s, StringComparer.Ordinal))
        {
            double Get(Dictionary<string, JsonNode?> m, string field) => m.TryGetValue(symbol, out var n) ? n![field]!.GetValue<double>() : 0;
            var da = methodsA.ToDictionary(p => p.Key, p => p.Value); var db = methodsB.ToDictionary(p => p.Key, p => p.Value);
            double before = Get(da, "ownShare"), after = Get(db, "ownShare");
            if (Math.Max(before, after) < 0.05) continue; // below 5 % of CPU on both sides: not worth a verdict
            double minA = Get(da, "minOwnShare"), maxA = Get(da, "maxOwnShare"), minB = Get(db, "minOwnShare"), maxB = Get(db, "maxOwnShare");
            deltas.Add(Judge(symbol + " (own CPU share)", before, after, runsA >= 3 && runsB >= 3, minA, maxA, minB, maxB, minAbsolute: 0.05));
        }
        var cpuA = a["runs"]!.AsArray().Where(r => r!["status"]!.GetValue<string>() == "completed").Select(r => r!["cpuMs"]!.GetValue<double>()).ToArray();
        var cpuB = b["runs"]!.AsArray().Where(r => r!["status"]!.GetValue<string>() == "completed").Select(r => r!["cpuMs"]!.GetValue<double>()).ToArray();
        if (cpuA.Length > 0 && cpuB.Length > 0)
            deltas.Add(Judge("total sampled CPU ms", cpuA.OrderBy(x => x).ElementAt(cpuA.Length / 2), cpuB.OrderBy(x => x).ElementAt(cpuB.Length / 2), cpuA.Length >= 3 && cpuB.Length >= 3, cpuA.Min(), cpuA.Max(), cpuB.Min(), cpuB.Max()));
        return deltas;
    }

    private static List<ProfileDelta> Counters(JsonNode a, JsonNode b)
    {
        var sa = a["series"]!.AsArray().ToDictionary(s => s!["name"]!.GetValue<string>());
        var sb = b["series"]!.AsArray().ToDictionary(s => s!["name"]!.GetValue<string>());
        var deltas = new List<ProfileDelta>();
        foreach (var (name, field) in new[] { ("allocationRate", "mean"), ("heapAfterGc", "last"), ("gcPauseFraction", "mean"), ("threadPoolQueueLength", "p95"), ("lockContentionsPerSecond", "mean"), ("cpuCores", "mean") })
            if (sa.TryGetValue(name, out var x) && sb.TryGetValue(name, out var y))
                deltas.Add(Judge($"{name} ({field})", x![field]!.GetValue<double>(), y![field]!.GetValue<double>(), repeated: false, 0, 0, 0, 0));
        return deltas;
    }

    private static List<ProfileDelta> Heap(JsonNode a, JsonNode b)
    {
        double Last(JsonNode r) => r["snapshots"]!.AsArray().LastOrDefault()?["totalBytes"]?.GetValue<double>() ?? 0;
        return new List<ProfileDelta> { Judge("reachable heap bytes (last snapshot)", Last(a), Last(b), repeated: false, 0, 0, 0, 0) };
    }

    private static ProfileDelta Judge(string subject, double before, double after, bool repeated, double minA, double maxA, double minB, double maxB, double minAbsolute = 0)
    {
        var change = before == 0 ? (after == 0 ? 0 : 100.0) : (after - before) / before * 100;
        var large = Math.Abs(change) >= RelativeThreshold * 100;
        if (!large) return new ProfileDelta(subject, Math.Round(before, 4), Math.Round(after, 4), Math.Round(change, 1), "noSignificantChange", "Change below 25 %.");
        if (Math.Abs(after - before) < minAbsolute)
            return new ProfileDelta(subject, Math.Round(before, 4), Math.Round(after, 4), Math.Round(change, 1), "noSignificantChange", $"A large relative change of a small quantity (less than {minAbsolute:P0} points): within noise.");
        if (!repeated) return new ProfileDelta(subject, Math.Round(before, 4), Math.Round(after, 4), Math.Round(change, 1), "inconclusive", "Large change, but a single run per side cannot be separated from run-to-run noise.");
        var disjoint = change > 0 ? minB > maxA : maxB < minA;
        if (!disjoint) return new ProfileDelta(subject, Math.Round(before, 4), Math.Round(after, 4), Math.Round(change, 1), "inconclusive", "Large change, but the ranges of the repeated runs overlap.");
        return new ProfileDelta(subject, Math.Round(before, 4), Math.Round(after, 4), Math.Round(change, 1), change > 0 ? "regression" : "improvement",
            "Large change and the repeated-run ranges are disjoint." + (change > 0 ? " (higher is worse for CPU/memory/contention)" : ""));
    }
}
