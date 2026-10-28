using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

sealed record CachedProjectAnalysis(string Key, Finding[] Findings, string[] Warnings, int Documents);

/// <summary>
/// Per-project result cache. The key covers every input that can change a result (rule-set version, settings,
/// all source texts, project file, package restore output), so a hit is only possible when re-analysis would
/// produce the same findings. Granularity is the project because a document's semantic results depend on the other
/// documents it binds against.
/// </summary>
sealed class AnalysisCache
{
    private readonly string directory;

    public AnalysisCache(string repositoryRoot)
    {
        var overrideDirectory = Environment.GetEnvironmentVariable("CODEATLAS_CACHE_DIR");
        var repoKey = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(repositoryRoot)))).ToLowerInvariant()[..16];
        var baseDirectory = !string.IsNullOrWhiteSpace(overrideDirectory)
            ? overrideDirectory
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData) is { Length: > 0 } appData
                ? appData
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config"), "CodeAtlas", "analysis");
        directory = Path.Combine(baseDirectory, repoKey);
    }

    public static string ComputeKey(string ruleSetVersion, string configurationFingerprint, string referenceFingerprint,
        string projectFileText, IEnumerable<(string Path, string Text)> documents)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        void Feed(string value) { hash.AppendData(Encoding.UTF8.GetBytes(value)); hash.AppendData(new byte[] { 0 }); }
        Feed(ruleSetVersion);
        Feed(configurationFingerprint);
        Feed(referenceFingerprint);
        Feed(projectFileText);
        foreach (var (path, text) in documents.OrderBy(d => d.Path, StringComparer.Ordinal))
        {
            Feed(path);
            Feed(FixBuilder.HashText(text));
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    public CachedProjectAnalysis? TryLoad(string projectRelativePath, string key)
    {
        try
        {
            var path = PathFor(projectRelativePath);
            if (!File.Exists(path)) return null;
            var cached = JsonSerializer.Deserialize<CachedProjectAnalysis>(File.ReadAllText(path), JsonDefaults.Options);
            return cached is not null && cached.Key == key ? cached : null;
        }
        catch { return null; } // A corrupt cache entry is a miss, never an error.
    }

    public void Save(string projectRelativePath, CachedProjectAnalysis entry)
    {
        try
        {
            Directory.CreateDirectory(directory);
            var path = PathFor(projectRelativePath);
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(entry, JsonDefaults.Options));
            File.Move(temporary, path, true);
        }
        catch { /* Caching is an optimization; failing to write must not fail the analysis. */ }
    }

    private string PathFor(string projectRelativePath) =>
        Path.Combine(directory, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(projectRelativePath))).ToLowerInvariant()[..16] + ".json");
}
