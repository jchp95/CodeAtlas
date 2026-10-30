using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

sealed record HeapOptions(ProfileTarget Target, int Snapshots, int IntervalSeconds, int WarmupSeconds, int TimeoutSeconds);

/// <summary>
/// Heap snapshots through dotnet-gcdump (reachable objects only) and their comparison. A type whose reachable bytes and count grow between
/// snapshots is a retention candidate; gcdump records no retention paths, so what holds the objects is a separate question.
/// </summary>
static partial class HeapProfiler
{
    [GeneratedRegex(@"^\s*(?<bytes>[\d,\.]+)\s+(?<count>[\d,\.]+)\s{2,}(?<type>.+?)(?:\s{2,}\[(?<module>[^\]]*)\])?\s*$")]
    private static partial Regex RowRegex();

    [GeneratedRegex(@"\s*\((?:Bytes\s*[><]=?\s*[\d\.]+[KMG]?|[^)]*Bytes[^)]*)\)\s*$")]
    private static partial Regex BucketSuffix();

    public static HeapProfileReport Run(string root, GraphIndex? index, HeapOptions options, CancellationToken cancellation)
    {
        var limitations = new[]
        {
            "gcdump lists reachable objects only (it forces a GC) and has no retention paths: it says what is kept, not who keeps it.",
            "The snapshot briefly suspends the process and contains type names and sizes, not object contents.",
            "Two snapshots show growth between two instants; growth while a cache warms up looks the same as a leak."
        };
        var tool = ProfilingTools.Find("dotnet-gcdump");
        if (tool is null) return Empty(root, options, "toolMissing", limitations, new[] { "dotnet-gcdump is not installed. " + ProfilingTools.InstallHint });

        var directory = ProfilingTools.PrepareOutputDirectory(root, "heap");
        var commands = new List<string>();
        var snapshots = new List<HeapSnapshot>();
        var notes = new List<string>();
        Process? child = null;
        try
        {
            int pid;
            if (options.Target.AttachProcessId is { } attached) pid = attached;
            else
            {
                var info = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, WorkingDirectory = root };
                info.ArgumentList.Add(options.Target.Program!);
                foreach (var a in options.Target.Arguments) info.ArgumentList.Add(a);
                child = Process.Start(info)!;
                child.OutputDataReceived += (_, _) => { }; child.ErrorDataReceived += (_, _) => { };
                child.BeginOutputReadLine(); child.BeginErrorReadLine();
                pid = child.Id;
            }

            if (!Wait(options.WarmupSeconds, cancellation)) return Cancelled(root, options, commands, snapshots, limitations);
            for (var i = 1; i <= options.Snapshots; i++)
            {
                if (child is { HasExited: true }) { notes.Add($"The process exited before snapshot {i}."); break; }
                var file = Path.Combine(directory, $"snapshot{i}.gcdump");
                var args = new[] { "collect", "-p", pid.ToString(CultureInfo.InvariantCulture), "-o", file, "--timeout", "120" };
                commands.Add("dotnet-gcdump " + string.Join(' ', args));
                var outcome = BoundedProcess.Run(tool, args, directory, TimeSpan.FromSeconds(options.TimeoutSeconds), cancellation, 10);
                if (outcome.Cancelled) return Cancelled(root, options, commands, snapshots, limitations);
                if (!File.Exists(file)) { notes.Add($"Snapshot {i} failed: " + string.Join(" | ", outcome.Tail.TakeLast(3))); continue; }

                var text = Capture(tool, new[] { "report", file }, directory, TimeSpan.FromSeconds(120), cancellation);
                var parsed = Parse(text);
                if (parsed is null) { notes.Add($"Snapshot {i}: the report could not be read."); continue; }
                snapshots.Add(parsed with { File = file });
                if (i < options.Snapshots && !Wait(options.IntervalSeconds, cancellation)) return Cancelled(root, options, commands, snapshots, limitations);
            }
        }
        finally
        {
            // Only a process this run started is stopped; an attached one is left exactly as it was.
            if (child is { HasExited: false }) { try { child.Kill(entireProcessTree: true); } catch { } }
            child?.Dispose();
        }

        if (snapshots.Count < 2) return new HeapProfileReport(1, "heap", root, DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss zzz"), snapshots.Count == 0 ? "noSnapshots" : "oneSnapshot", options.Target, commands.ToArray(),
            snapshots.ToArray(), Array.Empty<HeapGrowth>(), Array.Empty<Finding>(), limitations, notes.Append("At least two snapshots are needed to compare.").ToArray());

        var first = snapshots[0]; var last = snapshots[^1];
        var before = first.Types.ToDictionary(t => t.Type, StringComparer.Ordinal);
        var growth = last.Types.Select(t =>
        {
            before.TryGetValue(t.Type, out var b);
            var node = index?.Graph.Nodes.FirstOrDefault(n => !n.IsExternal && n.ContainingType == t.Type)?.Id;
            return new HeapGrowth(t.Type, b?.Bytes ?? 0, t.Bytes, b?.Count ?? 0, t.Count, t.Bytes - (b?.Bytes ?? 0), node);
        }).Where(g => g.GrowthBytes > 0).OrderByDescending(g => g.GrowthBytes).Take(25).ToArray();

        var findings = new List<Finding>();
        var totalGrowth = last.TotalBytes - first.TotalBytes;
        if (growth.Length > 0 && totalGrowth >= 10 * 1024 * 1024 && last.TotalBytes >= first.TotalBytes * 1.2 && growth[0].GrowthBytes >= totalGrowth * 0.5)
        {
            var top = growth[0];
            var location = CounterProfiler.TargetLocation(index, options.Target);
            findings.Add(GraphFindings.Create("PRF0301", FindingSeverity.Warning, ValidationLevel.Measured,
                $"Reachable {top.Type} grew from {top.CountBefore:N0} to {top.CountAfter:N0} objects",
                $"Between {snapshots.Count} snapshots the reachable heap grew from {first.TotalBytes / 1048576.0:N0} to {last.TotalBytes / 1048576.0:N0} MiB; {top.Type} explains {top.GrowthBytes / (double)totalGrowth:P0} of the growth ({top.GrowthBytes / 1048576.0:N0} MiB).",
                location,
                growth.Take(5).Select(g => new FindingEvidence("heap-growth", $"{g.Type}: {g.BytesBefore:N0} → {g.BytesAfter:N0} bytes, {g.CountBefore:N0} → {g.CountAfter:N0} objects")).ToArray(),
                null, "medium",
                new[] { "Two instants only: warm-up and caches grow too.", "gcdump has no retention paths: it names the type, not the owner." },
                "Objects that are still reachable cannot be freed; if growth continues the process runs out of memory.",
                "Find what stores these objects (static collections, caches without eviction, event handlers never unsubscribed, long-lived lists) by searching the code for places that add instances of this type; use a retention-path tool (PerfView or Visual Studio) on a dump if the owner is not obvious.",
                commands.FirstOrDefault() ?? "", $"{top.Type}", FindingOrigin.Profile));
        }
        foreach (var (snapshot, number) in snapshots.Select((s, i) => (s, i + 1)))
            if (Math.Abs(snapshot.TotalBytes - snapshot.AccountedBytes) > snapshot.TotalBytes * 0.1)
                notes.Add($"Snapshot {number}: the per-type sizes ({snapshot.AccountedBytes:N0}) differ from the reported heap size ({snapshot.TotalBytes:N0}) by more than 10 %; the gcdump may be incomplete on a large heap.");
        return new HeapProfileReport(1, "heap", root, DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss zzz"), "completed", options.Target, commands.ToArray(), snapshots.ToArray(), growth, findings.ToArray(), limitations, notes.ToArray());
    }

    /// <summary>Reads <c>dotnet-gcdump report</c>. "Object Bytes" is the size of one object of the row, so a row's total is bytes × count.</summary>
    internal static HeapSnapshot? Parse(string report)
    {
        long total = 0, objects = 0;
        var types = new Dictionary<string, (long Bytes, long Count)>(StringComparer.Ordinal);
        foreach (var line in report.Replace("\r", "").Split('\n'))
        {
            var heap = Regex.Match(line, @"^\s*([\d,\.]+)\s+GC Heap (bytes|objects)\s*$");
            if (heap.Success)
            {
                var value = long.Parse(heap.Groups[1].Value.Replace(",", "").Replace(".", ""), CultureInfo.InvariantCulture);
                if (heap.Groups[2].Value == "bytes") total = value; else objects = value;
                continue;
            }
            var row = RowRegex().Match(line);
            if (!row.Success || line.Contains("Object Bytes", StringComparison.Ordinal)) continue;
            var bytes = long.Parse(row.Groups["bytes"].Value.Replace(",", "").Replace(".", ""), CultureInfo.InvariantCulture);
            var count = long.Parse(row.Groups["count"].Value.Replace(",", "").Replace(".", ""), CultureInfo.InvariantCulture);
            var type = BucketSuffix().Replace(row.Groups["type"].Value.Trim(), "");
            types[type] = types.TryGetValue(type, out var existing) ? (existing.Bytes + bytes * count, existing.Count + count) : (bytes * count, count);
        }
        if (total == 0 || types.Count == 0) return null;
        return new HeapSnapshot("", total, objects, types.Sum(t => t.Value.Bytes), types.Select(p => new HeapType(p.Key, p.Value.Bytes, p.Value.Count)).OrderByDescending(t => t.Bytes).Take(300).ToArray());
    }

    private static bool Wait(int seconds, CancellationToken cancellation) => !cancellation.WaitHandle.WaitOne(TimeSpan.FromSeconds(seconds));

    private static string Capture(string file, string[] args, string cwd, TimeSpan timeout, CancellationToken cancellation)
    {
        var info = new ProcessStartInfo(file) { WorkingDirectory = cwd, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in args) info.ArgumentList.Add(a);
        using var process = Process.Start(info)!;
        var builder = new StringBuilder();
        var reader = Task.Run(() => { var buffer = new char[8192]; int n; while ((n = process.StandardOutput.Read(buffer, 0, buffer.Length)) > 0) { if (builder.Length < 8_000_000) builder.Append(buffer, 0, n); } });
        process.ErrorDataReceived += (_, _) => { }; process.BeginErrorReadLine();
        if (!process.WaitForExit(timeout)) { try { process.Kill(true); } catch { } }
        reader.Wait(5000);
        return builder.ToString();
    }

    private static HeapProfileReport Cancelled(string root, HeapOptions options, List<string> commands, List<HeapSnapshot> snapshots, string[] limitations) =>
        new(1, "heap", root, DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss zzz"), "cancelled", options.Target, commands.ToArray(), snapshots.ToArray(), Array.Empty<HeapGrowth>(), Array.Empty<Finding>(), limitations, Array.Empty<string>());

    private static HeapProfileReport Empty(string root, HeapOptions options, string status, string[] limitations, string[] notes) =>
        new(1, "heap", root, DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss zzz"), status, options.Target, Array.Empty<string>(), Array.Empty<HeapSnapshot>(), Array.Empty<HeapGrowth>(), Array.Empty<Finding>(), limitations, notes);
}
