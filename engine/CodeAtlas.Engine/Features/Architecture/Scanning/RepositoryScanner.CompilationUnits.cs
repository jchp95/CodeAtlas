using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

/// <summary>Source files, parsed trees and the best-effort Roslyn compilation for one .csproj.</summary>
sealed record ProjectCompilationUnit(string[] SourceFiles, SyntaxTree[] SyntaxTrees, CSharpCompilation Compilation);

/// <summary>A .NET project ready for analysis, with the metadata needed to key caches and name findings.</summary>
sealed record LoadedDotnetProject(
    string ProjectFile,
    string RelativeProjectFile,
    string Name,
    ProjectCompilationUnit Unit,
    string[] Warnings,
    string ReferenceFingerprint,
    string[] ProjectReferences);

sealed partial class RepositoryScanner
{
    /// <summary>Enumerates .csproj files using the same exclusion rules as the architecture scan.</summary>
    internal IEnumerable<string> FindDotnetProjectFiles() => EnumerateFiles(root, "*.csproj").OrderBy(p => p, StringComparer.Ordinal);

    internal string ToRelativePath(string path) => Relative(path);

    /// <summary>
    /// Builds the same compilation the architecture scan uses, so analyzer diagnostics and architecture
    /// evidence are derived from one semantic model instead of two diverging ones.
    /// </summary>
    internal LoadedDotnetProject LoadDotnetProject(string projectFile)
    {
        var warnings = new List<string>();
        var relative = Relative(projectFile);
        var name = Path.GetFileNameWithoutExtension(projectFile);
        var framework = DetectTargetFramework(SafeRead(projectFile));
        var unit = CreateProjectCompilation(projectFile, name, framework, relative, warnings, null, includeFrameworks: true);
        return new LoadedDotnetProject(projectFile, relative, name, unit, warnings.ToArray(), FingerprintReferences(projectFile), ReadProjectReferences(projectFile));
    }

    // Package restore output changes the semantic model (resolved symbols), so it is part of the cache key.
    private static string FingerprintReferences(string projectFile)
    {
        var assets = Path.Combine(Path.GetDirectoryName(projectFile)!, "obj", "project.assets.json");
        try
        {
            var info = new FileInfo(assets);
            return info.Exists ? $"{info.Length}:{info.LastWriteTimeUtc.Ticks}" : "no-assets";
        }
        catch { return "no-assets"; }
    }

    /// <summary>Repository-relative .csproj paths named by &lt;ProjectReference&gt; items that resolve to a file.</summary>
    internal string[] ReadProjectReferences(string projectFile)
    {
        try
        {
            var directory = Path.GetDirectoryName(projectFile)!;
            return System.Xml.Linq.XDocument.Parse(SafeRead(projectFile)).Descendants().Where(e => e.Name.LocalName == "ProjectReference")
                .Select(e => (string?)e.Attribute("Include")).Where(v => !string.IsNullOrWhiteSpace(v) && !v!.Contains("$(", StringComparison.Ordinal))
                .Select(v => Path.GetFullPath(Path.Combine(directory, v!.Replace('\\', Path.DirectorySeparatorChar))))
                .Where(File.Exists).Select(Relative).Distinct(StringComparer.Ordinal).OrderBy(v => v, StringComparer.Ordinal).ToArray();
        }
        catch { return Array.Empty<string>(); }
    }

    /// <summary>
    /// Loads every project with its ProjectReference compilations attached (transitively), so a call from one project
    /// into another binds to the real source symbol instead of failing as an unknown type. Projects are created in
    /// dependency order; a reference that would close a cycle is dropped (and reported) because a compilation cannot
    /// reference itself, which makes calls across that edge unresolved rather than silently guessed.
    /// </summary>
    internal IReadOnlyList<LoadedDotnetProject> LoadLinkedDotnetProjects(IEnumerable<string> projectFiles, List<string> warnings, CancellationToken cancellation)
    {
        var files = projectFiles.Select(Path.GetFullPath).Distinct(StringComparer.Ordinal).OrderBy(f => f, StringComparer.Ordinal).ToArray();
        var byRelative = files.ToDictionary(Relative, f => f, StringComparer.Ordinal);
        var references = files.ToDictionary(f => f, f => ReadProjectReferences(f).Where(byRelative.ContainsKey).Select(r => byRelative[r]).ToArray(), StringComparer.Ordinal);

        var order = new List<string>();
        var state = new Dictionary<string, int>(StringComparer.Ordinal); // 1 = visiting, 2 = done
        var dropped = new HashSet<(string From, string To)>();
        void Visit(string file)
        {
            state[file] = 1;
            foreach (var dependency in references[file])
            {
                if (!state.TryGetValue(dependency, out var s)) Visit(dependency);
                else if (s == 1) dropped.Add((file, dependency));
            }
            state[file] = 2;
            order.Add(file);
        }
        foreach (var file in files) if (!state.ContainsKey(file)) Visit(file);
        foreach (var (from, to) in dropped.OrderBy(d => d.From, StringComparer.Ordinal))
            warnings.Add($"Project reference cycle: {Relative(from)} -> {Relative(to)} was not linked into the compilation; calls across it are reported as unresolved.");

        var loaded = new Dictionary<string, LoadedDotnetProject>(StringComparer.Ordinal);
        foreach (var file in order)
        {
            cancellation.ThrowIfCancellationRequested();
            var closure = new List<MetadataReference>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            void Collect(string current)
            {
                foreach (var dependency in references[current])
                {
                    if (dropped.Contains((current, dependency)) || !loaded.TryGetValue(dependency, out var project) || !seen.Add(dependency)) continue;
                    closure.Add(project.Unit.Compilation.ToMetadataReference());
                    Collect(dependency);
                }
            }
            Collect(file);

            var projectWarnings = new List<string>();
            var relative = Relative(file);
            var name = Path.GetFileNameWithoutExtension(file);
            var unit = CreateProjectCompilation(file, name, DetectTargetFramework(SafeRead(file)), relative, projectWarnings, closure, includeFrameworks: true);
            loaded[file] = new LoadedDotnetProject(file, relative, name, unit, projectWarnings.ToArray(), FingerprintReferences(file), ReadProjectReferences(file));
        }
        warnings.AddRange(loaded.Values.SelectMany(p => p.Warnings));
        return files.Select(f => loaded[f]).ToArray();
    }

    internal IReadOnlyDictionary<string, string[]> BuildTypeIndex(List<string> warnings) => BuildRepositoryTypeIndex(warnings);
}
