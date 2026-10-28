using System.Diagnostics;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

static partial class HttpTestingCatalogBuilder
{
private static string? GetAttributeRoute(SyntaxList<AttributeListSyntax> lists, string name, IReadOnlyDictionary<string, string>? constants = null) =>
        lists.SelectMany(l => l.Attributes)
            .Where(a => AttributeName(a).Equals(name, StringComparison.OrdinalIgnoreCase))
            .Select(attribute => GetAttributeString(attribute, constants)).FirstOrDefault(value => value is not null);
    private static string? GetAttributeString(AttributeSyntax attribute, IReadOnlyDictionary<string, string>? constants = null) =>
        attribute.ArgumentList?.Arguments.Select(argument => ResolveStringExpression(argument.Expression, constants))
            .FirstOrDefault(value => value is not null);
    private static string AttributeName(AttributeSyntax attribute) => attribute.Name.ToString().Split('.').Last().Replace("Attribute", "", StringComparison.Ordinal);
    private static string? FirstString(ArgumentListSyntax args, IReadOnlyDictionary<string, string>? constants = null) =>
        args.Arguments.Select(a => ResolveStringExpression(a.Expression, constants)).FirstOrDefault(value => value is not null);

    private static string? ResolveStringExpression(ExpressionSyntax expression, IReadOnlyDictionary<string, string>? constants)
    {
        switch (expression)
        {
            case LiteralExpressionSyntax literal when literal.IsKind(SyntaxKind.StringLiteralExpression):
                return literal.Token.ValueText;
            case ParenthesizedExpressionSyntax parenthesized:
                return ResolveStringExpression(parenthesized.Expression, constants);
            case BinaryExpressionSyntax binary when binary.IsKind(SyntaxKind.AddExpression):
            {
                var left = ResolveStringExpression(binary.Left, constants);
                var right = ResolveStringExpression(binary.Right, constants);
                return left is not null && right is not null ? left + right : null;
            }
            case IdentifierNameSyntax identifier when constants is not null && constants.TryGetValue(identifier.Identifier.ValueText, out var value):
                return value;
            case MemberAccessExpressionSyntax member when constants is not null && constants.TryGetValue(member.ToString(), out var memberValue):
                return memberValue;
            default:
                return null;
        }
    }
    private static string InvokedName(ExpressionSyntax expression) => expression switch
    {
        MemberAccessExpressionSyntax m => m.Name.Identifier.ValueText,
        SimpleNameSyntax s => s.Identifier.ValueText,
        _ => expression.ToString().Split('.').LastOrDefault() ?? ""
    };
    private static bool TryHttpVerb(string name, out string verb)
    {
        var candidate = name.StartsWith("Http", StringComparison.OrdinalIgnoreCase) ? name[4..].ToUpperInvariant() : "";
        if (Methods.Contains(candidate) && candidate != "TRACE") { verb = candidate; return true; }
        verb = ""; return false;
    }
    private static bool TryMapVerb(string name, out string verb)
    {
        var candidate = name.StartsWith("Map", StringComparison.Ordinal) ? name[3..].ToUpperInvariant() : "";
        if (Methods.Contains(candidate)) { verb = candidate; return true; }
        verb = ""; return false;
    }
    private static int Line(SyntaxNode node) => node.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
    private static string CombineRoute(string prefix, string suffix) => NormalizeRoute(string.IsNullOrWhiteSpace(suffix) ? prefix : string.IsNullOrWhiteSpace(prefix) ? suffix : prefix.TrimEnd('/') + "/" + suffix.TrimStart('/'));
    private static string NormalizeRoute(string value)
    {
        var route = value.Trim().Replace("\\", "/", StringComparison.Ordinal);
        if (!route.StartsWith('/')) route = "/" + route;
        route = Regex.Replace(route, "/{2,}", "/");
        return route.Length > 1 ? route.TrimEnd('/') : route;
    }
    private static bool RouteMatches(string endpointRoute, string requestPath)
    {
        var expected = NormalizeRoute(endpointRoute).Split('/', StringSplitOptions.RemoveEmptyEntries);
        var actual = NormalizeRoute(requestPath).Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (expected.Length != actual.Length) return false;
        for (var i = 0; i < expected.Length; i++)
        {
            var part = expected[i];
            if (Regex.IsMatch(part, @"^\{[^}]+\}$") || Regex.IsMatch(part, @"^\[[^]]+\]$") || part.StartsWith(':')) continue;
            if (!part.Equals(actual[i], StringComparison.OrdinalIgnoreCase)) return false;
        }
        return true;
    }
    private static string NextAppRoute(string rel)
    {
        var start = rel.StartsWith("src/app/api/", StringComparison.Ordinal) ? "src/app".Length : "app".Length;
        var route = rel[start..].Replace("/route.tsx", "", StringComparison.OrdinalIgnoreCase)
            .Replace("/route.ts", "", StringComparison.OrdinalIgnoreCase).Replace("/route.jsx", "", StringComparison.OrdinalIgnoreCase)
            .Replace("/route.js", "", StringComparison.OrdinalIgnoreCase);
        route = Regex.Replace(route, @"\[\.\.\.(.+?)\]", "{$1}");
        return NormalizeRoute(Regex.Replace(route, @"\[([^\]]+)\]", "{$1}"));
    }
    private static string NextPagesRoute(string rel)
    {
        var start = rel.StartsWith("src/pages/api/", StringComparison.Ordinal) ? "src/pages".Length : "pages".Length;
        var route = Regex.Replace(rel[start..], @"\.(tsx?|jsx?)$", "", RegexOptions.IgnoreCase);
        route = Regex.Replace(route, @"/index$", "", RegexOptions.IgnoreCase);
        route = Regex.Replace(route, @"\[\.\.\.(.+?)\]", "{$1}");
        return NormalizeRoute(Regex.Replace(route, @"\[([^\]]+)\]", "{$1}"));
    }
    private static bool IsSource(string file) => new[] { ".cs", ".ts", ".tsx", ".js", ".jsx", ".mjs", ".cjs" }
        .Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase);
    private static string StableId(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant()[..20];
    private static string Relative(string root, string path) => Path.GetRelativePath(root, path).Replace('\\', '/');
    private static bool IsWithinOrSame(string path, string directory)
    {
        var p = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var d = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return p.Equals(d, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
            || p.StartsWith(d + Path.DirectorySeparatorChar, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }
    private static bool IsWithinNestedProject(string file, string projectDir, ProjectNode current, ProjectNode[] projects, string root)
    {
        foreach (var project in projects)
        {
            if (project.Id == current.Id) continue;
            var entry = Path.GetFullPath(Path.Combine(root, project.Path.Replace('/', Path.DirectorySeparatorChar)));
            var dir = Directory.Exists(entry) ? entry : Path.GetDirectoryName(entry) ?? root;
            if (dir.Length > projectDir.Length && IsWithinOrSame(dir, projectDir) && IsWithinOrSame(file, dir)) return true;
        }
        return false;
    }
    private static ProjectNode? FindOwningProject(string file, ProjectNode[] projects, string root)
    {
        var candidates = projects.Select(project =>
        {
            var entry = Path.GetFullPath(Path.Combine(root, project.Path.Replace('/', Path.DirectorySeparatorChar)));
            var directory = Directory.Exists(entry) ? entry : Path.GetDirectoryName(entry) ?? root;
            return (Project: project, Directory: directory);
        });
        return candidates.Where(candidate => IsWithinOrSame(file, candidate.Directory))
            .OrderByDescending(candidate => candidate.Directory.Length).Select(candidate => candidate.Project).FirstOrDefault();
    }

    private static string FindRepositoryPath(string file, string root, GitRepositoryState[] repositories)
    {
        var candidates = repositories.Select(repository =>
        {
            var directory = repository.Path == "." ? root
                : Path.GetFullPath(Path.Combine(root, repository.Path.Replace('/', Path.DirectorySeparatorChar)));
            return (repository.Path, Directory: directory);
        });
        return candidates.Where(candidate => IsWithinOrSame(file, candidate.Directory))
            .OrderByDescending(candidate => candidate.Directory.Length).Select(candidate => candidate.Path).FirstOrDefault() ?? ".";
    }
}
