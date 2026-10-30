using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

sealed record TestRunOptions(string? Project, string? Filter, string? RelatedSymbol, string? FailedFrom, bool NoBuild, int TimeoutSeconds);

/// <summary>
/// Runs <c>dotnet test</c> per test project and builds the report from the TRX files. A test counts as passed only if a TRX says so;
/// a project that did not build or produced no TRX is reported as such, and its discovered tests are listed as not run.
/// </summary>
static class TestRunner
{
    public static string RunsDirectory(string root)
    {
        var overrideDirectory = Environment.GetEnvironmentVariable("CODEATLAS_DATA_DIR");
        var baseDirectory = !string.IsNullOrWhiteSpace(overrideDirectory) ? overrideDirectory
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData) is { Length: > 0 } a ? a : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config"), "CodeAtlas");
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(root)))).ToLowerInvariant()[..16];
        return Path.Combine(baseDirectory, "testruns", key);
    }

    /// <summary>The set of tests a run would execute and the exact commands, without executing anything.</summary>
    public static (List<(string ProjectKey, string File)> Projects, string? Filter, string[] Notes, string? Error) Plan(string root, GraphIndex index, TestRunOptions options)
    {
        var notes = new List<string>();
        var discovered = TestDiscovery.Discover(index);
        var projects = discovered.Select(t => t.Project).Distinct(StringComparer.Ordinal)
            .Where(p => options.Project is null || p.Equals(options.Project, StringComparison.OrdinalIgnoreCase) || (index.Graph.ProjectFiles.GetValueOrDefault(p) ?? "").Equals(options.Project, StringComparison.OrdinalIgnoreCase))
            .Select(p => (ProjectKey: p, File: index.Graph.ProjectFiles.GetValueOrDefault(p) ?? "")).Where(p => p.File.Length > 0).ToList();
        if (projects.Count == 0) return (projects, null, notes.ToArray(), options.Project is null ? "No test methods were found in the analyzed projects." : $"No test project matches '{options.Project}'.");

        var filter = options.Filter;
        if (options.RelatedSymbol is not null)
        {
            var impact = ImpactAnalyzer.Analyze(index, options.RelatedSymbol, new GraphLimits(), null);
            if (impact.Status != "ok") return (projects, null, notes.ToArray(), $"Symbol '{options.RelatedSymbol}' is {impact.Status}; pick one with an id (see 'impact').");
            var related = impact.RelatedTests.Select(t => index.Nodes[t.Node.Id]).Select(n => (n.ContainingType ?? "", n.Name)).ToList();
            if (impact.Symbol is { } subject && index.Nodes[subject.Id].Tags.Contains("test")) related.Add((index.Nodes[subject.Id].ContainingType ?? "", index.Nodes[subject.Id].Name));
            if (related.Count == 0) return (projects, null, notes.ToArray(), "No tests reach this symbol through the call graph; nothing was selected (running everything would not be 'related tests').");
            filter = TestDiscovery.Filter(related);
            notes.Add($"Selected {related.Distinct().Count()} test method(s) that reach the symbol ({(impact.Exhaustive ? "exhaustive" : "known consumers only")}).");
            var keep = related.Select(r => discovered.FirstOrDefault(d => d.Class == r.Item1 && d.Method == r.Item2)?.Project).Where(p => p is not null).ToHashSet();
            projects = projects.Where(p => keep.Contains(p.ProjectKey)).ToList();
        }
        else if (options.FailedFrom is not null)
        {
            var previous = JsonSerializer.Deserialize<TestRunReport>(File.ReadAllText(options.FailedFrom), JsonDefaults.Options);
            var failed = previous?.Projects.SelectMany(p => p.Results).Where(r => r.Outcome == TestOutcome.Failed).Select(r => (r.Class, r.Method)).ToList() ?? new();
            if (failed.Count == 0) return (projects, null, notes.ToArray(), "The previous report has no failed tests to repeat.");
            filter = TestDiscovery.Filter(failed);
            var failedProjects = previous!.Projects.Where(p => p.Results.Any(r => r.Outcome == TestOutcome.Failed)).Select(p => p.Project).ToHashSet();
            projects = projects.Where(p => failedProjects.Contains(p.ProjectKey)).ToList();
            notes.Add($"Repeating {failed.Distinct().Count()} failed test method(s) from {options.FailedFrom}.");
        }
        return (projects, filter, notes.ToArray(), null);
    }

    public static TestRunReport Run(string root, GraphIndex index, TestRunOptions options, CancellationToken cancellation)
    {
        var (projects, filter, notes, error) = Plan(root, index, options);
        var runDirectory = Path.Combine(RunsDirectory(root), DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss"));
        if (error is not null) return Empty(root, "nothingToRun", filter, runDirectory, notes.Append(error).ToArray());
        Directory.CreateDirectory(runDirectory);

        var runProjects = new List<TestRunProject>();
        Cobertura? coverage = null; string? coverageFile = null;
        foreach (var project in projects)
        {
            cancellation.ThrowIfCancellationRequested();
            var projectFile = Path.Combine(root, project.File);
            var results = Path.Combine(runDirectory, project.ProjectKey);
            var wantsCoverage = File.ReadAllText(projectFile).Contains("coverlet.collector", StringComparison.OrdinalIgnoreCase);
            var arguments = new List<string> { "test", projectFile, "--logger", $"trx;LogFileName={project.ProjectKey}.trx", "--results-directory", results };
            if (options.NoBuild) arguments.Add("--no-build");
            if (filter is not null) { arguments.Add("--filter"); arguments.Add(filter); }
            if (wantsCoverage) { arguments.Add("--collect"); arguments.Add("XPlat Code Coverage"); }
            var command = "dotnet " + string.Join(' ', arguments.Select(a => a.Contains(' ') || a.Contains('|') ? $"\"{a}\"" : a));

            var outcome = BoundedProcess.Run("dotnet", arguments, root, TimeSpan.FromSeconds(options.TimeoutSeconds), cancellation);
            var trx = Directory.Exists(results) ? Directory.GetFiles(results, "*.trx", SearchOption.AllDirectories).FirstOrDefault() : null;
            var cobertura = Directory.Exists(results) ? Directory.GetFiles(results, "coverage.cobertura.xml", SearchOption.AllDirectories).FirstOrDefault() : null;
            var status = outcome.Cancelled ? "cancelled" : outcome.TimedOut ? "timedOut" : trx is null ? (outcome.Tail.Any(l => l.Contains("error", StringComparison.OrdinalIgnoreCase)) ? "buildFailed" : "noResults") : "completed";

            var rows = new List<TestResult>();
            if (trx is not null)
                foreach (var r in TrxParser.Parse(trx))
                    rows.Add(new TestResult(r.Name, r.Class, r.Method, project.ProjectKey, r.Outcome, r.Raw, Math.Round(r.DurationMs, 1), r.Message, r.StackTrace,
                        StackTraceResolver.Resolve(r.StackTrace, index, root)));
            if (cobertura is not null) { coverageFile = cobertura; coverage = Cobertura.Load(cobertura, root); }
            runProjects.Add(new TestRunProject(project.ProjectKey, project.File, status, command, outcome.ExitCode, outcome.DurationMs, trx, cobertura, rows.ToArray(),
                status == "completed" ? outcome.Tail.TakeLast(8).ToArray() : outcome.Tail));
        }

        return Assemble(root, index, filter, runDirectory, runProjects, coverage, coverageFile, notes);
    }

    private static TestRunReport Assemble(string root, GraphIndex index, string? filter, string runDirectory, List<TestRunProject> projects, Cobertura? coverage, string? coverageFile, string[] planNotes)
    {
        var all = projects.SelectMany(p => p.Results).ToArray();
        int Count(TestOutcome o) => all.Count(r => r.Outcome == o);
        var summary = new TestRunSummary(Count(TestOutcome.Passed), Count(TestOutcome.Failed), Count(TestOutcome.Skipped), Count(TestOutcome.NotExecuted), Count(TestOutcome.Other), Math.Round(projects.Sum(p => p.DurationMs), 1));

        // Discovered in source, selected by the plan, but absent from every result: say so instead of implying they passed.
        var executedMethods = all.Select(r => $"{r.Project}|{r.Class}.{r.Method}").ToHashSet(StringComparer.Ordinal);
        var attempted = projects.Select(p => p.Project).ToHashSet(StringComparer.Ordinal);
        var notRun = TestDiscovery.Discover(index).Where(t => attempted.Contains(t.Project) && !executedMethods.Contains($"{t.Project}|{t.Class}.{t.Method}"))
            .Where(t => filter is null || filter.Contains($"{t.Class}.{t.Method}", StringComparison.Ordinal))
            .Select(t => $"{t.Project}: {t.Class}.{t.Method}").ToArray();

        var findings = new List<Finding>();
        foreach (var result in all.Where(r => r.Outcome == TestOutcome.Failed)) findings.Add(FailureFinding(root, index, projects.First(p => p.Project == result.Project), result));
        foreach (var project in projects.Where(p => p.Status != "completed"))
            findings.Add(GraphFindings.Create("TST0002", FindingSeverity.Warning, ValidationLevel.RuntimeObserved, $"{project.Project}: tests did not run ({project.Status})",
                $"`dotnet test` for {project.Project} ended with status {project.Status}; no TRX results exist, so none of its tests can be reported as passed.",
                new FindingLocation(project.ProjectFile, 1, 1, 1, null, project.Project),
                project.OutputTail.TakeLast(10).Select(l => new FindingEvidence("output", l)).Append(new FindingEvidence("command", project.Command)).ToArray(), null, "high",
                new[] { "Only the last lines of output are kept." }, "Without results the behavior of this project is unknown.", "Fix the reported error (usually a build error) and run again.",
                project.Command, $"{project.Project}|{project.Status}", FindingOrigin.Test));

        CoverageSummary? coverageSummary = null;
        var notes = planNotes.ToList();
        if (coverage is not null)
        {
            var analysis = new CodeAnalysisEngine(root, new AnalysisOptions()).Run(CancellationToken.None);
            var measured = analysis.Findings.Select(f => new CoveredFinding(f.Id, f.RuleId, f.Location.File, f.Location.Line, coverage.StateOf(f.Location.File, f.Location.Line)))
                .Where(c => c.Coverage != "noData").Take(200).ToArray();
            coverageSummary = new CoverageSummary(coverageFile!, Math.Round(coverage.LineRate, 4), coverage.LinesCovered, coverage.LinesValid, measured);
            notes.Add("Coverage comes from the coverlet Cobertura report of this run. A finding marked 'uncovered' sits on a line no executed test reached; 'notInstrumented' means the line holds no executable code.");
        }
        else notes.Add("No coverage was collected: add the coverlet.collector package to a test project to enable it.");
        var status = projects.Count == 0 ? "nothingToRun" : projects.Any(p => p.Status is "cancelled") ? "cancelled" : projects.All(p => p.Status == "completed") ? (summary.Failed > 0 ? "completedWithFailures" : "completed") : "incomplete";
        notes.Add("Outcomes come from TRX files only. 'notExecuted' is how the TRX reports skipped/ignored tests; they are neither passed nor failed.");
        return new TestRunReport(1, root, DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss zzz"), status, filter, runDirectory, projects.ToArray(), summary, notRun, coverageSummary, findings.ToArray(), notes.ToArray());
    }

    private static Finding FailureFinding(string root, GraphIndex index, TestRunProject project, TestResult result)
    {
        var frames = result.Frames;
        // The origin of the failure is the first frame (the top of the stack) that is user code; assertion libraries and the runner are external.
        var origin = frames.FirstOrDefault(f => f.NodeId is not null && !(index.Nodes[f.NodeId].Tags.Contains("test"))) ?? frames.FirstOrDefault(f => f.NodeId is not null);
        var originNode = origin?.NodeId is { } id ? index.Nodes[id] : null;
        var location = origin?.File is { } file && origin.Line is { } line
            ? new FindingLocation(file, line, 1, line, originNode is null ? null : $"{originNode.ContainingType}.{originNode.Signature}", originNode?.Project ?? project.Project)
            : originNode is not null ? GraphFindings.LocationOf(originNode) : new FindingLocation(project.ProjectFile, 1, 1, 1, result.Name, project.Project);
        var path = frames.Where(f => f.NodeId is not null).Select(f => index.Step(index.Nodes[f.NodeId!], "direct", f.File is null || f.Line is null ? null : new GraphLocation(f.File, f.Line.Value))).ToArray();
        var rerun = $"dotnet test {project.ProjectFile} --filter \"FullyQualifiedName~{result.Class}.{result.Method}\"";
        return GraphFindings.Create("TST0001", FindingSeverity.Error, ValidationLevel.RuntimeObserved, $"Test failed: {result.Class}.{result.Method}",
            (result.Message ?? "The test failed without a message.").Split('\n')[0].Trim(),
            location,
            new[] { new FindingEvidence("test-result", $"{result.Name}: {result.RawOutcome} in {result.DurationMs} ms (from {Path.GetFileName(project.TrxFile)})", null, (result.Message ?? "").Split('\n').FirstOrDefault()?.Trim()) }
                .Concat(frames.Take(6).Select(f => new FindingEvidence("stack-frame", $"{f.Symbol} [{f.Resolution}]", f.File is null || f.Line is null ? null : new FindingLocation(f.File, f.Line.Value, 1, f.Line.Value, null, project.Project)))).ToArray(),
            path.Length == 0 ? null : path, frames.Any(f => f.Resolution == "resolved") ? "high" : "medium",
            new[] { "The failure was observed in this environment and run; a flaky test can pass on another run.", "The top user frame is where the exception surfaced, not necessarily where the defect is (the test, its data or the code under test may be wrong).", frames.Any(f => f.File is null) ? "Some frames carry no file/line (no symbols); they were matched to source by name only." : "" }.Where(x => x.Length > 0).ToArray(),
            "A behavior the test expects does not hold for the tested code path.",
            originNode is null ? "Inspect the failure message and rerun the test under a debugger." : $"Start at {originNode.ContainingType}.{originNode.Signature}; use 'impact' on it to see who else depends on that behavior.",
            rerun, $"{project.Project}|{result.Class}.{result.Method}", FindingOrigin.Test);
    }

    private static TestRunReport Empty(string root, string status, string? filter, string runDirectory, string[] notes) =>
        new(1, root, DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss zzz"), status, filter, runDirectory, Array.Empty<TestRunProject>(), new TestRunSummary(0, 0, 0, 0, 0, 0),
            Array.Empty<string>(), null, Array.Empty<Finding>(), notes);
}
