using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

sealed record AnalysisOptions(bool UseCache = true, string[]? RuleIds = null, int? MaxParallelism = null);

/// <summary>
/// Runs the rule catalog over every .NET project of a repository. Projects are processed one at a time to bound
/// memory (a compilation is large); documents of a project are analyzed in parallel against one shared compilation.
/// </summary>
sealed class CodeAnalysisEngine
{
    private readonly string root;
    private readonly AnalysisOptions options;

    public CodeAnalysisEngine(string root, AnalysisOptions options)
    {
        this.root = root;
        this.options = options;
    }

    public static IReadOnlyList<RuleDescriptor> Descriptors => RuleCatalog.All.Select(r => r.Descriptor).ToArray();

    public AnalysisReport Run(CancellationToken cancellation)
    {
        var total = Stopwatch.StartNew();
        var configuration = AnalysisConfiguration.Load(root);
        var warnings = new List<string>(configuration.Warnings);
        var rules = SelectRules(warnings);
        var parallelism = Math.Clamp(options.MaxParallelism ?? Environment.ProcessorCount, 1, 16);
        var cache = new AnalysisCache(root);
        var scanner = new RepositoryScanner(root);

        var findings = new List<Finding>();
        var summaries = new List<AnalysisProjectSummary>();
        var ruleTicks = new long[rules.Count];
        var ruleFindings = new int[rules.Count];
        long compilationTicks = 0;
        int documentsAnalyzed = 0, fromCache = 0;
        var cancelled = false;

        try
        {
            foreach (var projectFile in scanner.FindDotnetProjectFiles())
            {
                cancellation.ThrowIfCancellationRequested();
                var loadTimer = Stopwatch.StartNew();
                var project = scanner.LoadDotnetProject(projectFile);
                compilationTicks += loadTimer.ElapsedTicks;
                warnings.AddRange(project.Warnings);

                var documents = project.Unit.SourceFiles.Zip(project.Unit.SyntaxTrees)
                    .Select(pair => (Full: pair.First, Relative: scanner.ToRelativePath(pair.First), Tree: pair.Second))
                    .Where(d => !configuration.IsExcluded(d.Relative) && !IsGenerated(d.Tree))
                    .ToArray();

                var key = AnalysisCache.ComputeKey(RuleCatalog.Version + "|" + string.Join(",", rules.Select(r => r.Descriptor.Id)),
                    configuration.Fingerprint, project.ReferenceFingerprint, File.ReadAllText(projectFile),
                    documents.Select(d => (d.Relative, d.Tree.GetText(cancellation).ToString())));

                var cached = options.UseCache ? cache.TryLoad(project.RelativeProjectFile, key) : null;
                if (cached is not null)
                {
                    fromCache++;
                    findings.AddRange(cached.Findings);
                    warnings.AddRange(cached.Warnings);
                    summaries.Add(new AnalysisProjectSummary(project.Name, project.RelativeProjectFile, cached.Documents, cached.Findings.Length, true, key));
                    continue;
                }

                var projectWarnings = new List<string>();
                var drafts = AnalyzeDocuments(project, documents.Select(d => (d.Relative, d.Tree)).ToArray(), rules, configuration,
                    parallelism, ruleTicks, projectWarnings, cancellation);
                var projectFindings = FinalizeIds(drafts);
                documentsAnalyzed += documents.Length;
                foreach (var finding in projectFindings)
                {
                    var index = rules.ToList().FindIndex(r => r.Descriptor.Id == finding.RuleId);
                    if (index >= 0) ruleFindings[index]++;
                }
                findings.AddRange(projectFindings);
                warnings.AddRange(projectWarnings);
                summaries.Add(new AnalysisProjectSummary(project.Name, project.RelativeProjectFile, documents.Length, projectFindings.Length, false, key));
                if (options.UseCache) cache.Save(project.RelativeProjectFile, new CachedProjectAnalysis(key, projectFindings, projectWarnings.ToArray(), documents.Length));
            }
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
            warnings.Add("The analysis was cancelled; results cover only the projects completed before cancellation.");
        }

        total.Stop();
        var timings = rules.Select((rule, i) => new RuleTiming(rule.Descriptor.Id, Math.Round(ruleTicks[i] * 1000.0 / Stopwatch.Frequency, 2), ruleFindings[i])).ToArray();
        var telemetry = new AnalysisTelemetry(
            Math.Round(total.Elapsed.TotalMilliseconds, 1), Math.Round(compilationTicks * 1000.0 / Stopwatch.Frequency, 1),
            summaries.Count - fromCache, fromCache, documentsAnalyzed, Environment.WorkingSet / (1024 * 1024), parallelism, timings);

        return new AnalysisReport(1, root, DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss zzz"), RuleCatalog.Version,
            summaries.ToArray(), FindingMerger.Merge(findings), rules.Select(r => r.Descriptor).ToArray(),
            warnings.Distinct().ToArray(), cancelled, telemetry);
    }

    private IReadOnlyList<ICodeRule> SelectRules(List<string> warnings)
    {
        if (options.RuleIds is not { Length: > 0 } requested) return RuleCatalog.All;
        var known = RuleCatalog.All.ToDictionary(r => r.Descriptor.Id, StringComparer.OrdinalIgnoreCase);
        foreach (var id in requested.Where(id => !known.ContainsKey(id)))
            warnings.Add($"Unknown rule id '{id}' was ignored.");
        return requested.Where(known.ContainsKey).Select(id => known[id]).ToArray();
    }

    private static bool IsGenerated(SyntaxTree tree)
    {
        var first = tree.GetRoot().GetLeadingTrivia().ToString();
        return first.Contains("<auto-generated", StringComparison.OrdinalIgnoreCase) || first.Contains("<autogenerated", StringComparison.OrdinalIgnoreCase);
    }

    private static ConcurrentBag<FindingDraft> AnalyzeDocuments(LoadedDotnetProject project, (string Relative, SyntaxTree Tree)[] documents,
        IReadOnlyList<ICodeRule> rules, AnalysisConfiguration configuration, int parallelism, long[] ruleTicks,
        List<string> warnings, CancellationToken cancellation)
    {
        var sink = new ConcurrentBag<FindingDraft>();
        var failures = new ConcurrentBag<string>();
        var unresolved = 0;
        var compilation = project.Unit.Compilation;

        Parallel.ForEach(documents, new ParallelOptions { MaxDegreeOfParallelism = parallelism, CancellationToken = cancellation }, document =>
        {
            var model = compilation.GetSemanticModel(document.Tree, ignoreAccessibility: true);
            for (var i = 0; i < rules.Count; i++)
            {
                cancellation.ThrowIfCancellationRequested();
                var rule = rules[i];
                var timer = Stopwatch.StartNew();
                try
                {
                    rule.Analyze(new RuleContext(project.Name, document.Relative, document.Tree, model, rule.Descriptor,
                        configuration.For(rule.Descriptor.Id), sink, cancellation));
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    // A faulty rule must never take the whole analysis down, but it must not be silent either.
                    failures.Add($"Rule {rule.Descriptor.Id} failed on {document.Relative}: {ex.GetType().Name}: {ex.Message}");
                }
                Interlocked.Add(ref ruleTicks[i], timer.ElapsedTicks);
            }

            var missing = model.GetDiagnostics(null, cancellation).Count(d => d.Id is "CS0246" or "CS0234" or "CS0012" or "CS0400");
            if (missing > 0) Interlocked.Add(ref unresolved, missing);
        });

        warnings.AddRange(failures.Distinct());
        if (unresolved > 0)
            warnings.Add($"{project.Name}: {unresolved} unresolved type/assembly reference(s). Run 'dotnet restore' so package symbols resolve; rules that depend on those symbols were skipped for the affected expressions.");
        return sink;
    }

    /// <summary>Assigns stable ids once all rules ran; identical offending code in one symbol is told apart by source order.</summary>
    private static Finding[] FinalizeIds(ConcurrentBag<FindingDraft> drafts)
    {
        var ordinals = new Dictionary<string, int>(StringComparer.Ordinal);
        var result = new List<Finding>();
        foreach (var draft in drafts.OrderBy(d => d.Finding.Location.File, StringComparer.Ordinal).ThenBy(d => d.Position).ThenBy(d => d.Finding.RuleId, StringComparer.Ordinal))
        {
            var finding = draft.Finding;
            var baseKey = $"{finding.RuleId}|{finding.Location.Project}|{finding.Location.File}|{finding.Location.Symbol}|{draft.Fingerprint}";
            var ordinal = ordinals.GetValueOrDefault(baseKey);
            ordinals[baseKey] = ordinal + 1;
            result.Add(finding with
            {
                Id = FindingIdentity.Create(finding.RuleId, finding.Location.Project, finding.Location.File, finding.Location.Symbol, draft.Fingerprint, ordinal)
            });
        }
        return result.ToArray();
    }
}
