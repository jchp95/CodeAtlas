using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

sealed record CachedFragment(string Key, ProjectFragment Fragment);

/// <summary>Per-project call-graph fragment cache. A key covers the project's sources and everything it can bind against.</summary>
sealed class FragmentCache
{
    private readonly string directory;

    public FragmentCache(string repositoryRoot)
    {
        var overrideDirectory = Environment.GetEnvironmentVariable("CODEATLAS_CACHE_DIR");
        var repoKey = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(repositoryRoot)))).ToLowerInvariant()[..16];
        var baseDirectory = !string.IsNullOrWhiteSpace(overrideDirectory)
            ? overrideDirectory
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData) is { Length: > 0 } appData
                ? appData
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config"), "CodeAtlas");
        directory = Path.Combine(baseDirectory, "callgraph", repoKey);
    }

    public ProjectFragment? TryLoad(string projectFile, string key)
    {
        try
        {
            var path = PathFor(projectFile);
            if (!File.Exists(path)) return null;
            var cached = JsonSerializer.Deserialize<CachedFragment>(File.ReadAllText(path), JsonDefaults.Options);
            return cached is not null && cached.Key == key ? cached.Fragment : null;
        }
        catch { return null; }
    }

    public void Save(string projectFile, string key, ProjectFragment fragment)
    {
        try
        {
            Directory.CreateDirectory(directory);
            var path = PathFor(projectFile);
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(new CachedFragment(key, fragment), JsonDefaults.Options));
            File.Move(temporary, path, true);
        }
        catch { /* Optimization only. */ }
    }

    public static string ComputeKey(string version, GraphLimits limits, string projectFileText, string referenceFingerprint,
        IEnumerable<(string Path, string Text)> documents, IEnumerable<string> dependencyKeys)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        void Feed(string value) { hash.AppendData(Encoding.UTF8.GetBytes(value)); hash.AppendData(new byte[] { 0 }); }
        Feed(version);
        Feed(limits.MaxDispatchTargets.ToString());
        Feed(projectFileText);
        Feed(referenceFingerprint);
        foreach (var (path, text) in documents.OrderBy(d => d.Path, StringComparer.Ordinal)) { Feed(path); Feed(FixBuilder.HashText(text)); }
        foreach (var key in dependencyKeys.OrderBy(k => k, StringComparer.Ordinal)) Feed(key);
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private string PathFor(string projectFile) =>
        Path.Combine(directory, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(projectFile))).ToLowerInvariant()[..16] + ".json");
}
