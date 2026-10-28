using System.Text.Json;
using System.Text.RegularExpressions;

/// <summary>Per-rule overrides read from <c>codeatlas.analysis.json</c> at the repository root.</summary>
sealed record RuleSettings(bool Enabled, FindingSeverity? Severity, int? Threshold);

/// <summary>
/// Example:
/// { "exclude": ["**/Generated/**"], "rules": { "ATL1009": { "severity": "error", "threshold": 20 }, "ATL1010": { "enabled": false } } }
/// </summary>
sealed class AnalysisConfiguration
{
    private static readonly string[] DefaultExcludes =
    {
        "**/obj/**", "**/bin/**", "**/Migrations/**", "**/*.g.cs", "**/*.generated.cs", "**/*.Designer.cs", "**/*AssemblyInfo.cs"
    };

    private readonly Dictionary<string, RuleSettings> rules = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<Regex> excludes = new();

    public string Fingerprint { get; private set; } = "default";
    public List<string> Warnings { get; } = new();

    public static AnalysisConfiguration Load(string repositoryRoot)
    {
        var configuration = new AnalysisConfiguration();
        foreach (var pattern in DefaultExcludes) configuration.excludes.Add(GlobToRegex(pattern));

        var path = Path.Combine(repositoryRoot, "codeatlas.analysis.json");
        if (!File.Exists(path)) return configuration;

        try
        {
            var text = File.ReadAllText(path);
            configuration.Fingerprint = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text)))[..16];
            using var document = JsonDocument.Parse(text, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            var root = document.RootElement;

            if (root.TryGetProperty("exclude", out var exclude) && exclude.ValueKind == JsonValueKind.Array)
                foreach (var item in exclude.EnumerateArray().Where(i => i.ValueKind == JsonValueKind.String))
                    configuration.excludes.Add(GlobToRegex(item.GetString()!));

            if (root.TryGetProperty("rules", out var ruleElement) && ruleElement.ValueKind == JsonValueKind.Object)
            {
                foreach (var rule in ruleElement.EnumerateObject())
                {
                    var enabled = !(rule.Value.TryGetProperty("enabled", out var e) && e.ValueKind == JsonValueKind.False);
                    FindingSeverity? severity = null;
                    if (rule.Value.TryGetProperty("severity", out var s) && s.ValueKind == JsonValueKind.String)
                    {
                        if (Enum.TryParse<FindingSeverity>(s.GetString(), true, out var parsed)) severity = parsed;
                        else configuration.Warnings.Add($"codeatlas.analysis.json: unknown severity '{s.GetString()}' for {rule.Name}; using the default.");
                    }
                    int? threshold = rule.Value.TryGetProperty("threshold", out var t) && t.TryGetInt32(out var value) ? value : null;
                    configuration.rules[rule.Name] = new RuleSettings(enabled, severity, threshold);
                }
            }
        }
        catch (Exception ex)
        {
            configuration.Warnings.Add($"codeatlas.analysis.json could not be read ({ex.Message}); default settings are in effect.");
        }

        return configuration;
    }

    public RuleSettings For(string ruleId) => rules.TryGetValue(ruleId, out var settings) ? settings : new RuleSettings(true, null, null);

    public bool IsExcluded(string relativePath) => excludes.Any(regex => regex.IsMatch(relativePath));

    private static Regex GlobToRegex(string glob)
    {
        var pattern = Regex.Escape(glob.Replace('\\', '/')).Replace(@"\*\*/", "(?:.*/)?").Replace(@"\*\*", ".*").Replace(@"\*", "[^/]*").Replace(@"\?", "[^/]");
        return new Regex("^" + pattern + "$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    }
}
