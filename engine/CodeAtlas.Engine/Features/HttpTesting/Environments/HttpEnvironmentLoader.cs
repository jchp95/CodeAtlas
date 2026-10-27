using System.Diagnostics;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

static class HttpEnvironmentLoader
{
    public static HttpEnvironmentInfo Inspect(string root, string requestDirectory) => Load(root, requestDirectory, null);

    public static Dictionary<string, string> LoadSelectedEnvironment(string root, string requestDirectory, string environmentName)
    {
        var info = Load(root, requestDirectory, environmentName);
        return info.SelectedValues;
    }

    private static HttpEnvironmentInfo Load(string root, string requestDirectory, string? selectedName)
    {
        var environmentNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var variableNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var sharedValues = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var selectedValues = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in EnvironmentFiles(root, requestDirectory))
        {
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(file));
                if (document.RootElement.ValueKind != JsonValueKind.Object) continue;
                if (document.RootElement.TryGetProperty("$shared", out var shared))
                {
                    CollectNames(shared, variableNames);
                    ReadValues(shared, sharedValues);
                }
                foreach (var environment in document.RootElement.EnumerateObject())
                {
                    if (environment.Name.Equals("$shared", StringComparison.Ordinal) || environment.Value.ValueKind != JsonValueKind.Object) continue;
                    environmentNames.Add(environment.Name);
                    CollectNames(environment.Value, variableNames);
                    if (selectedName is not null && environment.Name.Equals(selectedName, StringComparison.OrdinalIgnoreCase))
                        ReadValues(environment.Value, selectedValues);
                }
            }
            catch { /* optional Rider environment files can be edited while CodeAtlas is running */ }
        }
        foreach (var pair in sharedValues) selectedValues.TryAdd(pair.Key, pair.Value);
        return new HttpEnvironmentInfo(environmentNames.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray(), variableNames,
            sharedValues, selectedValues);
    }

    private static IEnumerable<string> EnvironmentFiles(string root, string requestDirectory)
    {
        var current = Path.GetFullPath(requestDirectory);
        var fullRoot = Path.GetFullPath(root);
        var directories = new List<string>();
        while (Inside(current, fullRoot))
        {
            directories.Add(current);
            if (current.Equals(fullRoot, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) break;
            current = Path.GetDirectoryName(current) ?? fullRoot;
        }
        // Parent workspaces provide defaults; a repository-local file and then its private file win.
        foreach (var directory in directories.AsEnumerable().Reverse())
        {
            foreach (var name in new[] { "http-client.env.json", "http-client.private.env.json" })
            {
                var path = Path.Combine(directory, name);
                if (File.Exists(path)) yield return path;
            }
        }
    }
    private static void CollectNames(JsonElement element, HashSet<string> names)
    {
        if (element.ValueKind != JsonValueKind.Object) return;
        foreach (var property in element.EnumerateObject()) names.Add(property.Name);
    }
    private static void ReadValues(JsonElement element, Dictionary<string, string> target)
    {
        if (element.ValueKind != JsonValueKind.Object) return;
        foreach (var property in element.EnumerateObject())
            target[property.Name] = property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString() ?? "" : property.Value.ToString();
    }
    private static bool Inside(string path, string root) => path.Equals(root, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
        || path.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}
