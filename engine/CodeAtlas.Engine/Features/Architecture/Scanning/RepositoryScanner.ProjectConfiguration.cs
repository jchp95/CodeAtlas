using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

sealed partial class RepositoryScanner
{
private static void DetectHttpTargets(string content, string relativeFile, List<HttpTargetCandidate> targets)
    {
        var pattern = new Regex("""https?://[^\s"'<>),;}\]]+""", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        foreach (Match match in pattern.Matches(content))
        {
            var url = match.Value.TrimEnd('.', ',', ';', ']', '}', ')');
            if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && !string.IsNullOrWhiteSpace(uri.Host))
                targets.Add(new HttpTargetCandidate(uri.GetLeftPart(UriPartial.Path).TrimEnd('/'), relativeFile));
        }
    }

    private string[] FindListenAddresses(string projectDirectory)
    {
        var addresses = new List<string>();
        foreach (var file in EnumerateFiles(projectDirectory, "launchSettings.json"))
        {
            try
            {
                using var document = JsonDocument.Parse(SafeRead(file));
                if (!document.RootElement.TryGetProperty("profiles", out var profiles) || profiles.ValueKind != JsonValueKind.Object) continue;
                foreach (var profile in profiles.EnumerateObject())
                {
                    if (profile.Value.ValueKind != JsonValueKind.Object) continue;
                    if (!profile.Value.TryGetProperty("applicationUrl", out var applicationUrl) || applicationUrl.ValueKind != JsonValueKind.String) continue;
                    addresses.AddRange((applicationUrl.GetString() ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
                }
            }
            catch { /* launch settings are optional and may contain environment-specific values */ }
        }
        return addresses.Distinct(StringComparer.OrdinalIgnoreCase).Take(30).ToArray();
    }

    private static bool HostMatchesProject(string host, ProjectNode target)
    {
        var hostName = CanonicalName(host.Split('.')[0]);
        if (hostName.Length < 5) return false;
        var pathParts = target.Path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var aliases = new List<string> { target.Name, Path.GetFileNameWithoutExtension(target.Path) };
        aliases.AddRange(pathParts.Where(part => !part.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)
            && part is not "src" and not "app" and not "api" and not "services"));
        return aliases.Select(CanonicalName).Any(alias => alias.Length >= 5 &&
            (hostName.Contains(alias, StringComparison.OrdinalIgnoreCase) || alias.Contains(hostName, StringComparison.OrdinalIgnoreCase)));
    }

    private static string CanonicalName(string value) => Regex.Replace(value.ToLowerInvariant(), "[^a-z0-9]", "");

    private string[] FindNextRouteCandidates(string packageDirectory)
    {
        var routes = new List<string>();
        foreach (var routeRootName in new[] { "app", "src/app", "pages", "src/pages" })
        {
            var routeRoot = Path.Combine(packageDirectory, routeRootName.Replace('/', Path.DirectorySeparatorChar));
            if (!Directory.Exists(routeRoot)) continue;
            var isAppRouter = routeRootName.EndsWith("app", StringComparison.Ordinal);
            foreach (var file in EnumerateFiles(routeRoot, "*.*"))
            {
                if (!IsSupportedSourceFile(file)) continue;
                var filename = Path.GetFileName(file);
                var isPage = filename.StartsWith("page.", StringComparison.OrdinalIgnoreCase);
                var isApi = filename.StartsWith("route.", StringComparison.OrdinalIgnoreCase);
                var isLayout = filename.StartsWith("layout.", StringComparison.OrdinalIgnoreCase);
                var isPagesRouterCandidate = !filename.StartsWith("_", StringComparison.Ordinal)
                    && !filename.EndsWith(".d.ts", StringComparison.OrdinalIgnoreCase);
                if (isAppRouter ? !(isPage || isApi || isLayout) : !isPagesRouterCandidate) continue;

                var routeBase = Path.ChangeExtension(Path.GetRelativePath(routeRoot, file), null)?.Replace('\\', '/') ?? string.Empty;
                var routeDirectory = Path.GetDirectoryName(routeBase)?.Replace('\\', '/') ?? string.Empty;
                var routeLeaf = Path.GetFileName(routeBase);
                var route = isAppRouter
                    ? (routeDirectory is "" or "." ? "/" : "/" + routeDirectory)
                    : routeLeaf.Equals("index", StringComparison.OrdinalIgnoreCase)
                        ? (routeDirectory is "" or "." ? "/" : "/" + routeDirectory)
                        : "/" + routeBase;
                if (isApi) routes.Add($"API {route} · App Router · {Relative(file)}");
                else if (isPage) routes.Add($"PAGE {route} · App Router · {Relative(file)}");
                else if (isLayout) routes.Add($"LAYOUT {route} · App Router · {Relative(file)}");
                else if (route.StartsWith("/api/", StringComparison.OrdinalIgnoreCase) || route.Equals("/api", StringComparison.OrdinalIgnoreCase))
                    routes.Add($"API {route} · Pages Router · {Relative(file)}");
                else routes.Add($"PAGE {route} · Pages Router · {Relative(file)}");
                if (routes.Count >= 250) return routes.Distinct().ToArray();
            }
        }
        return routes.Distinct().ToArray();
    }

    private IEnumerable<string> EnumerateFiles(string directory, string pattern)
    {
        if (!Directory.Exists(directory)) yield break;
        var stack = new Stack<string>();
        stack.Push(directory);
        while (stack.Count > 0)
        {
            var current = stack.Pop();
            string[] files;
            string[] subdirectories;
            try
            {
                files = Directory.GetFiles(current, pattern, SearchOption.TopDirectoryOnly);
                subdirectories = Directory.GetDirectories(current, "*", SearchOption.TopDirectoryOnly);
            }
            catch { continue; }

            foreach (var file in files) yield return file;
            foreach (var subdirectory in subdirectories)
            {
                var name = Path.GetFileName(subdirectory);
                if (ExcludedDirectories.Contains(name) || (name.StartsWith(".", StringComparison.Ordinal) && name != ".well-known")) continue;
                stack.Push(subdirectory);
            }
        }
    }

    private static readonly Lazy<MetadataReference[]> PlatformMetadataReferences = new(() =>
    {
        var trustedAssemblies = AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string;
        if (string.IsNullOrWhiteSpace(trustedAssemblies)) return Array.Empty<MetadataReference>();
        return trustedAssemblies.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(path =>
            {
                try { return MetadataReference.CreateFromFile(path); }
                catch { return null; }
            })
            .Where(reference => reference is not null).Cast<MetadataReference>().ToArray();
    });

    private static IReadOnlyList<MetadataReference> LoadMetadataReferences(string projectDirectory, string? framework)
    {
        var references = new Dictionary<string, MetadataReference>(StringComparer.OrdinalIgnoreCase);
        foreach (var reference in PlatformMetadataReferences.Value)
        {
            if (reference is PortableExecutableReference pe && pe.FilePath is { } path)
                references[MetadataReferenceKey(path)] = reference;
        }

        var assetsPath = Path.Combine(projectDirectory, "obj", "project.assets.json");
        if (!File.Exists(assetsPath)) return references.Values.ToArray();
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(assetsPath));
            var root = document.RootElement;
            if (!root.TryGetProperty("packageFolders", out var packageFolders)
                || !root.TryGetProperty("libraries", out var libraries)
                || !root.TryGetProperty("targets", out var targets)) return references.Values.ToArray();

            var packageRoots = packageFolders.EnumerateObject().Select(p => p.Name).ToArray();
            if (packageRoots.Length == 0) return references.Values.ToArray();
            var target = targets.EnumerateObject().FirstOrDefault(t =>
                string.IsNullOrWhiteSpace(framework) || t.Name.Contains(framework, StringComparison.OrdinalIgnoreCase)
                || FrameworkMatchesAssetTarget(framework, t.Name));
            if (target.Value.ValueKind != JsonValueKind.Object)
                target = targets.EnumerateObject().FirstOrDefault();
            if (target.Value.ValueKind != JsonValueKind.Object) return references.Values.ToArray();

            foreach (var libraryTarget in target.Value.EnumerateObject())
            {
                if (!libraries.TryGetProperty(libraryTarget.Name, out var libraryMetadata)
                    || !libraryMetadata.TryGetProperty("type", out var typeElement)
                    || typeElement.GetString() != "package"
                    || !libraryMetadata.TryGetProperty("path", out var pathElement)) continue;
                var libraryPath = pathElement.GetString();
                if (string.IsNullOrWhiteSpace(libraryPath)
                    || !libraryTarget.Value.TryGetProperty("compile", out var compileAssets)
                    || compileAssets.ValueKind != JsonValueKind.Object) continue;

                foreach (var compileAsset in compileAssets.EnumerateObject())
                {
                    var assetName = compileAsset.Name;
                    if (!assetName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) || assetName.EndsWith("/_._", StringComparison.Ordinal)) continue;
                    foreach (var packageRoot in packageRoots)
                    {
                        var candidate = Path.GetFullPath(Path.Combine(packageRoot, libraryPath.Replace('/', Path.DirectorySeparatorChar), assetName.Replace('/', Path.DirectorySeparatorChar)));
                        if (!File.Exists(candidate)) continue;
                        var identity = MetadataReferenceKey(candidate);
                        if (!references.ContainsKey(identity))
                        {
                            try { references[identity] = MetadataReference.CreateFromFile(candidate); }
                            catch { /* Ignore malformed, incompatible, or transient package assets. */ }
                        }
                        break;
                    }
                }
            }
        }
        catch
        {
            // project.assets.json can be stale or incomplete while a project is being restored.
        }
        return references.Values.ToArray();
    }

    private static string MetadataReferenceKey(string path)
    {
        try { return System.Reflection.AssemblyName.GetAssemblyName(path).Name ?? path; }
        catch { return path; }
    }

    private static bool FrameworkMatchesAssetTarget(string framework, string targetName)
    {
        var digits = Regex.Match(framework, @"^(?:net|netcoreapp)([0-9]+(?:\.[0-9]+)?)", RegexOptions.IgnoreCase).Groups[1].Value;
        return digits.Length > 0 && targetName.Contains($"v{digits}", StringComparison.OrdinalIgnoreCase);
    }

    private string Relative(string path) => PathTools.Relative(root, path);
    private static string NormalizePath(string path) => path.Replace('\\', '/').TrimStart('/');
    private static string SafeRead(string path) { try { return File.ReadAllText(path); } catch { return string.Empty; } }
    private static bool IsSupportedSourceFile(string path) =>
        new[] { ".ts", ".tsx", ".js", ".jsx", ".mjs", ".cjs" }.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    private static string? DetectTargetFramework(string content)
    {
        try
        {
            var document = XDocument.Parse(content);
            var frameworks = document.Descendants().Where(e => e.Name.LocalName is "TargetFramework" or "TargetFrameworks")
                .Select(e => e.Value.Trim()).Where(v => v.Length > 0).Distinct().ToArray();
            return frameworks.Length == 0 ? null : string.Join("; ", frameworks);
        }
        catch
        {
            var match = Regex.Match(content, "<TargetFrameworks?>([^<]+)</TargetFrameworks?>", RegexOptions.IgnoreCase);
            return match.Success ? match.Groups[1].Value.Trim() : null;
        }
    }

    private static GitChange[] ParseGitStatus(string output) => output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
        .Select(line => line.Length >= 4 ? new GitChange(line[..2].Trim() is { Length: > 0 } status ? status : "M", line[3..].Trim()) : null)
        .Where(change => change is not null).Cast<GitChange>().ToArray();
}
