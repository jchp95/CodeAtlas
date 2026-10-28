using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.CodeAnalysis;

sealed record CallGraphOptions(GraphLimits Limits, bool UseCache = true, int? MaxParallelism = null);

/// <summary>
/// Builds the solution call graph: per-project extraction (parallel, cached), then a global assembly step that resolves
/// interface/virtual dispatch against every implementation known in the analyzed solution.
/// </summary>
sealed class CallGraphBuilder
{
    /// <summary>Bumped whenever extraction or assembly semantics change so cached fragments are discarded.</summary>
    public const string Version = "1.0.0";

    private readonly string root;
    private readonly CallGraphOptions options;

    public CallGraphBuilder(string root, CallGraphOptions options)
    {
        this.root = root;
        this.options = options;
    }

    public CallGraph Build(CancellationToken cancellation)
    {
        var total = Stopwatch.StartNew();
        var warnings = new List<string>();
        var scanner = new RepositoryScanner(root);
        var cancelled = false;

        var compilationTimer = Stopwatch.StartNew();
        IReadOnlyList<LoadedDotnetProject> loaded = Array.Empty<LoadedDotnetProject>();
        try { loaded = scanner.LoadLinkedDotnetProjects(scanner.FindDotnetProjectFiles(), warnings, cancellation); }
        catch (OperationCanceledException) { cancelled = true; }
        compilationTimer.Stop();

        var keys = ProjectKeys(loaded);
        var identity = new GraphIdentity(loaded.ToDictionary(p => p.Unit.Compilation.AssemblyName ?? p.Name, p => keys[p.ProjectFile], StringComparer.Ordinal));
        var lazyIndex = new Lazy<IReadOnlyDictionary<string, string[]>>(() => scanner.BuildTypeIndex(new List<string>()), LazyThreadSafetyMode.ExecutionAndPublication);
        var cache = new FragmentCache(root);

        var extractionTimer = Stopwatch.StartNew();
        var fragments = new ConcurrentDictionary<string, ProjectFragment>(StringComparer.Ordinal);
        var fromCache = 0;
        if (!cancelled)
        {
            var projectKeysForCache = CacheKeys(loaded, keys);
            try
            {
                Parallel.ForEach(loaded, new ParallelOptions { MaxDegreeOfParallelism = Math.Clamp(options.MaxParallelism ?? Math.Min(Environment.ProcessorCount, 4), 1, 16), CancellationToken = cancellation }, project =>
                {
                    var key = projectKeysForCache[project.ProjectFile];
                    var cached = options.UseCache ? cache.TryLoad(project.RelativeProjectFile, key) : null;
                    if (cached is not null)
                    {
                        Interlocked.Increment(ref fromCache);
                        fragments[project.ProjectFile] = cached;
                        return;
                    }
                    var fragment = new ProjectFragmentBuilder(root, project, keys[project.ProjectFile], identity, lazyIndex, cancellation).Build();
                    fragments[project.ProjectFile] = fragment;
                    if (options.UseCache) cache.Save(project.RelativeProjectFile, key, fragment);
                });
            }
            catch (OperationCanceledException) { cancelled = true; }
        }
        extractionTimer.Stop();

        var assemblyTimer = Stopwatch.StartNew();
        var assembled = new CallGraphAssembler(options.Limits).Assemble(fragments.Values.OrderBy(f => f.Project, StringComparer.Ordinal).ToArray());
        assemblyTimer.Stop();
        warnings.AddRange(fragments.Values.SelectMany(f => f.Warnings));
        warnings.AddRange(assembled.Warnings);
        if (cancelled) warnings.Add("The graph build was cancelled; the graph covers only the projects completed before cancellation and is not complete.");

        var status = cancelled ? GraphStatus.Cancelled : assembled.LimitNotes.Length > 0 && assembled.HitHardLimit ? GraphStatus.LimitReached : GraphStatus.Completed;
        total.Stop();
        var metrics = new GraphMetrics(fragments.Count, assembled.Nodes.Length, assembled.Nodes.Count(n => !n.IsExternal), assembled.Nodes.Count(n => n.IsExternal),
            assembled.Edges.Length, assembled.EntryPoints.Length, fromCache, Math.Round(total.Elapsed.TotalMilliseconds, 1),
            Math.Round(compilationTimer.Elapsed.TotalMilliseconds, 1), Math.Round(extractionTimer.Elapsed.TotalMilliseconds, 1), Math.Round(assemblyTimer.Elapsed.TotalMilliseconds, 1));

        return new CallGraph(1, root, DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss zzz"), Version, status,
            assembled.Completeness(cancelled), keys.Values.OrderBy(k => k, StringComparer.Ordinal).ToArray(),
            loaded.OrderBy(p => keys[p.ProjectFile], StringComparer.Ordinal)
                .Select(p => new[] { keys[p.ProjectFile] }.Concat(p.ProjectReferences.Select(r => ProjectKeyForRelative(loaded, keys, r))).ToArray()).ToArray(),
            assembled.Nodes, assembled.Edges, assembled.EntryPoints, assembled.LimitNotes, metrics, warnings.Distinct(StringComparer.Ordinal).ToArray());
    }

    /// <summary>Project name when unique, repository-relative csproj path otherwise, so identities never collide.</summary>
    private static Dictionary<string, string> ProjectKeys(IReadOnlyList<LoadedDotnetProject> loaded)
    {
        var duplicates = loaded.GroupBy(p => p.Name, StringComparer.Ordinal).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet(StringComparer.Ordinal);
        return loaded.ToDictionary(p => p.ProjectFile, p => duplicates.Contains(p.Name) ? p.RelativeProjectFile : p.Name, StringComparer.Ordinal);
    }

    private static string ProjectKeyForRelative(IReadOnlyList<LoadedDotnetProject> loaded, Dictionary<string, string> keys, string relative) =>
        loaded.Where(p => p.RelativeProjectFile == relative).Select(p => keys[p.ProjectFile]).FirstOrDefault() ?? relative;

    private Dictionary<string, string> CacheKeys(IReadOnlyList<LoadedDotnetProject> loaded, Dictionary<string, string> keys)
    {
        var own = loaded.ToDictionary(p => p.ProjectFile, p => FragmentCache.ComputeKey(Version, options.Limits, File.ReadAllText(p.ProjectFile), p.ReferenceFingerprint,
            p.Unit.SyntaxTrees.Select(t => (t.FilePath, t.GetText().ToString())), new[] { keys[p.ProjectFile] }), StringComparer.Ordinal);
        var byRelative = loaded.ToDictionary(p => p.RelativeProjectFile, StringComparer.Ordinal);

        // A project binds against everything it references transitively, so its fragment is only reusable when all of those are unchanged.
        IEnumerable<string> Closure(LoadedDotnetProject project)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var stack = new Stack<LoadedDotnetProject>();
            stack.Push(project);
            while (stack.Count > 0)
                foreach (var reference in stack.Pop().ProjectReferences)
                    if (byRelative.TryGetValue(reference, out var dependency) && seen.Add(reference)) { stack.Push(dependency); yield return own[dependency.ProjectFile]; }
        }
        return loaded.ToDictionary(p => p.ProjectFile, p => FragmentCache.ComputeKey(Version, options.Limits, own[p.ProjectFile], "closure", Array.Empty<(string, string)>(), Closure(p)), StringComparer.Ordinal);
    }
}
