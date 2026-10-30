using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;

sealed partial class RepositoryScanner
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, MetadataReference[]> FrameworkReferenceCache = new(StringComparer.Ordinal);

    /// <summary>
    /// ASP.NET Core is a FrameworkReference, not a package: it never appears in project.assets.json, so a Web project would
    /// bind none of MapGet, Results, WebApplication, IServiceCollection… Reference assemblies from the installed targeting
    /// pack (or, failing that, the shared runtime) are attached for projects that use the Web SDK or reference the framework.
    /// Used by the call graph and the analyzer; the architecture scan keeps its original references.
    /// </summary>
    private static IReadOnlyList<MetadataReference> LoadFrameworkReferences(string projectContent, string? framework)
    {
        if (!UsesAspNetCore(projectContent)) return Array.Empty<MetadataReference>();
        var major = Regex.Match(framework ?? "", @"net(?:coreapp)?(\d+)", RegexOptions.IgnoreCase).Groups[1].Value;
        return FrameworkReferenceCache.GetOrAdd(major, _ => FindAspNetCoreReferences(major));
    }

    private static bool UsesAspNetCore(string projectContent)
    {
        try
        {
            var document = XDocument.Parse(projectContent);
            if (((string?)document.Root?.Attribute("Sdk"))?.Contains("Microsoft.NET.Sdk.Web", StringComparison.OrdinalIgnoreCase) == true) return true;
            return document.Descendants().Any(e => e.Name.LocalName == "FrameworkReference"
                && ((string?)e.Attribute("Include"))?.Equals("Microsoft.AspNetCore.App", StringComparison.OrdinalIgnoreCase) == true);
        }
        catch { return false; }
    }

    private static MetadataReference[] FindAspNetCoreReferences(string major)
    {
        try
        {
            // <root>/shared/Microsoft.NETCore.App/<version>/ -> <root>
            var root = Path.GetFullPath(Path.Combine(System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", ".."));
            string? Newest(string directory) => !Directory.Exists(directory) ? null : Directory.GetDirectories(directory)
                .Where(d => major.Length == 0 || Path.GetFileName(d).StartsWith(major + ".", StringComparison.Ordinal))
                .OrderByDescending(d => Version.TryParse(new string(Path.GetFileName(d).TakeWhile(c => char.IsDigit(c) || c == '.').ToArray()), out var v) ? v : new Version(0, 0))
                .FirstOrDefault();

            var pack = Newest(Path.Combine(root, "packs", "Microsoft.AspNetCore.App.Ref"));
            var refDirectory = pack is null ? null : Directory.GetDirectories(Path.Combine(pack, "ref")).OrderByDescending(d => d, StringComparer.Ordinal).FirstOrDefault();
            var source = refDirectory ?? Newest(Path.Combine(root, "shared", "Microsoft.AspNetCore.App"));
            if (source is null) return Array.Empty<MetadataReference>();
            return Directory.GetFiles(source, "*.dll").OrderBy(f => f, StringComparer.Ordinal)
                .Select(file => { try { return MetadataReference.CreateFromFile(file); } catch { return null; } })
                .Where(r => r is not null).Cast<MetadataReference>().ToArray();
        }
        catch { return Array.Empty<MetadataReference>(); }
    }
}
