using System.Text.Json;
using System.Text.RegularExpressions;

sealed record LayerDefinition(string Name, string[] Projects, string[] Namespaces);

sealed record ArchitectureRule(string Id, string Type, string[] From, string[] To, FindingSeverity Severity, string? Reason,
    string? Module, string[] Namespaces, string[] PublicNamespaces, string? Scope);

sealed record ArchitectureException(string Rule, string From, string To, string Reason);

/// <summary>
/// Solution-level architecture rules from <c>codeatlas.architecture.json</c>. Nothing is assumed: without the file no
/// layer rule runs, and there is no built-in Clean Architecture, DDD or Vertical Slice convention.
/// <code>
/// { "layers": [ { "name": "Domain", "projects": ["*.Domain"], "namespaces": ["Shop.Domain*"] } ],
///   "rules": [ { "id": "domain-isolated", "type": "forbidden", "from": "Domain", "to": ["Application","Infrastructure"], "severity": "error", "reason": "…" },
///              { "id": "app-allowed", "type": "allowedOnly", "from": "Application", "to": ["Domain"] },
///              { "id": "no-billing-shipping", "type": "forbiddenProjects", "from": "Billing.*", "to": "Shipping.*" },
///              { "id": "billing-boundary", "type": "moduleBoundary", "module": "Billing", "namespaces": ["Billing*"], "publicNamespaces": ["Billing.Contracts*"] },
///              { "id": "acyclic-layers", "type": "noCycles", "scope": "layers" } ],
///   "exceptions": [ { "rule": "domain-isolated", "from": "Shop.Web.*", "to": "Shop.Infrastructure*", "reason": "composition root" } ] }
/// </code>
/// </summary>
sealed class ArchitectureConfig
{
    public static readonly string[] RuleTypes = { "forbidden", "allowedOnly", "forbiddenProjects", "moduleBoundary", "noCycles" };

    public string? FilePath { get; private set; }
    public List<LayerDefinition> Layers { get; } = new();
    public List<ArchitectureRule> Rules { get; } = new();
    public List<ArchitectureException> Exceptions { get; } = new();
    public List<string> Warnings { get; } = new();
    public bool Invalid { get; private set; }

    public static ArchitectureConfig Load(string root)
    {
        var config = new ArchitectureConfig();
        var path = Path.Combine(root, "codeatlas.architecture.json");
        if (!File.Exists(path)) return config;
        config.FilePath = "codeatlas.architecture.json";
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            var json = document.RootElement;
            if (json.TryGetProperty("layers", out var layers) && layers.ValueKind == JsonValueKind.Array)
                foreach (var layer in layers.EnumerateArray())
                {
                    var name = Text(layer, "name");
                    if (name is null) { config.Warnings.Add("A layer without a name was ignored."); continue; }
                    config.Layers.Add(new LayerDefinition(name, List(layer, "projects"), List(layer, "namespaces")));
                }
            var layerNames = config.Layers.Select(l => l.Name).ToHashSet(StringComparer.Ordinal);

            if (json.TryGetProperty("rules", out var rules) && rules.ValueKind == JsonValueKind.Array)
                foreach (var rule in rules.EnumerateArray())
                {
                    var id = Text(rule, "id");
                    var type = Text(rule, "type");
                    if (id is null || type is null || !RuleTypes.Contains(type)) { config.Warnings.Add($"Rule '{id ?? "(no id)"}' has a missing id or unknown type '{type}' and was ignored. Known types: {string.Join(", ", RuleTypes)}."); continue; }
                    var from = List(rule, "from");
                    var to = List(rule, "to");
                    var unknown = from.Concat(to).Where(n => type is "forbidden" or "allowedOnly" && !layerNames.Contains(n)).Distinct().ToArray();
                    if (unknown.Length > 0) { config.Warnings.Add($"Rule '{id}' names undefined layer(s) {string.Join(", ", unknown)} and was ignored."); continue; }
                    var severity = Enum.TryParse<FindingSeverity>(Text(rule, "severity"), true, out var parsed) ? parsed : FindingSeverity.Error;
                    config.Rules.Add(new ArchitectureRule(id, type, from, to, severity, Text(rule, "reason"), Text(rule, "module"), List(rule, "namespaces"), List(rule, "publicNamespaces"), Text(rule, "scope")));
                }

            if (json.TryGetProperty("exceptions", out var exceptions) && exceptions.ValueKind == JsonValueKind.Array)
                foreach (var exception in exceptions.EnumerateArray())
                {
                    var reason = Text(exception, "reason");
                    if (string.IsNullOrWhiteSpace(reason)) { config.Warnings.Add($"An exception for rule '{Text(exception, "rule")}' has no reason and was ignored; exceptions must be justified."); continue; }
                    config.Exceptions.Add(new ArchitectureException(Text(exception, "rule") ?? "*", Text(exception, "from") ?? "*", Text(exception, "to") ?? "*", reason));
                }
        }
        catch (Exception ex)
        {
            config.Invalid = true;
            config.Warnings.Add($"codeatlas.architecture.json could not be read ({ex.Message}); no architecture rules were applied.");
        }
        return config;
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static string[] List(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value)) return Array.Empty<string>();
        return value.ValueKind switch
        {
            JsonValueKind.String => new[] { value.GetString()! },
            JsonValueKind.Array => value.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.String).Select(v => v.GetString()!).ToArray(),
            _ => Array.Empty<string>()
        };
    }

    public static bool Glob(string pattern, string value) =>
        Regex.IsMatch(value, "^" + Regex.Escape(pattern).Replace(@"\*", ".*").Replace(@"\?", ".") + "$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static bool AnyGlob(IEnumerable<string> patterns, string? value) => value is not null && patterns.Any(p => Glob(p, value));

    public string? LayerOfProject(string project) =>
        Layers.FirstOrDefault(l => AnyGlob(l.Projects, project))?.Name;

    public string? LayerOfNode(GraphNode node) =>
        (node.Namespace is null ? null : Layers.FirstOrDefault(l => AnyGlob(l.Namespaces, node.Namespace))?.Name) ?? LayerOfProject(node.Project);
}
