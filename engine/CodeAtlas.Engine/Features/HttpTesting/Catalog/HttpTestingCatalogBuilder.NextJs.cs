using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Text.RegularExpressions;

static partial class HttpTestingCatalogBuilder
{
    private static IEnumerable<ApiEndpoint> ScanNext(ProjectNode project, string projectDirectory, string[] files,
        string root, GitRepositoryState[] repositories)
    {
        foreach (var file in files.Where(f => new[] { ".ts", ".tsx", ".js", ".jsx" }.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase)))
        {
            var rel = Relative(projectDirectory, file).Replace('\\', '/');
            var appRouter = rel.StartsWith("app/api/", StringComparison.Ordinal) || rel.StartsWith("src/app/api/", StringComparison.Ordinal);
            var pagesRouter = rel.StartsWith("pages/api/", StringComparison.Ordinal) || rel.StartsWith("src/pages/api/", StringComparison.Ordinal);
            if (!appRouter && !pagesRouter) continue;
            if (appRouter && !Path.GetFileName(file).StartsWith("route.", StringComparison.OrdinalIgnoreCase)) continue;
            var route = appRouter ? NextAppRoute(rel) : NextPagesRoute(rel);
            var content = File.ReadAllText(file);
            var relative = Relative(root, file);
            var methodMatches = Regex.Matches(content,
                @"\bexport\s+(?:async\s+)?(?:function\s+(GET|POST|PUT|PATCH|DELETE|HEAD|OPTIONS)\b|const\s+(GET|POST|PUT|PATCH|DELETE|HEAD|OPTIONS)\s*=)", RegexOptions.IgnoreCase).Cast<Match>().ToArray();
            var methods = methodMatches.Select(m => (m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value).ToUpperInvariant())
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (methods.Length == 0 && pagesRouter) methods = new[] { "ANY" };
            foreach (var method in methods)
            {
                var match = methodMatches.FirstOrDefault(m => (m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value).Equals(method, StringComparison.OrdinalIgnoreCase));
                var line = match is null ? 1 : 1 + content[..match.Index].Count(c => c == '\n');
                yield return MakeEndpoint(project, root, repositories, appRouter ? "Next.js App Router" : "Next.js Pages API",
                    method, route, relative, line, null, new Dictionary<string, BaseTypeDeclarationSyntax[]>(),
                    Array.Empty<string>(), "Método exportado detectado en la ruta API.");
            }
        }
    }
}
