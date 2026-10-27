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
private static IEnumerable<HttpCallCandidate> ScanOutgoingHttpCalls(ProjectNode project, string[] files, string root,
        GitRepositoryState[] repositories)
    {
        foreach (var file in files.Where(f => new[] { ".cs", ".ts", ".tsx", ".js", ".jsx", ".mjs", ".cjs" }
                     .Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase)))
        {
            var relative = Relative(root, file).Replace('\\', '/');
            if (relative.Split('/').Any(segment => segment.Contains("test", StringComparison.OrdinalIgnoreCase)
                || segment.Contains("spec", StringComparison.OrdinalIgnoreCase))) continue;
            if (Path.GetExtension(file).Equals(".cs", StringComparison.OrdinalIgnoreCase))
            {
                SyntaxTree tree;
                try { tree = CSharpSyntaxTree.ParseText(File.ReadAllText(file), new CSharpParseOptions(LanguageVersion.Preview), file); }
                catch { continue; }
                var syntaxRoot = tree.GetRoot();
                foreach (var invocation in syntaxRoot.DescendantNodes().OfType<InvocationExpressionSyntax>())
                {
                    if (!TryGetCSharpHttpCall(invocation, syntaxRoot, out var method, out var route)) continue;
                    var className = invocation.Ancestors().OfType<BaseTypeDeclarationSyntax>().FirstOrDefault()?.Identifier.ValueText;
                    var methodName = invocation.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault()?.Identifier.ValueText;
                    var symbol = string.Join(".", new[] { className, methodName }.Where(value => !string.IsNullOrWhiteSpace(value)));
                    if (symbol.Length == 0) symbol = "Llamada HTTP";
                    yield return new HttpCallCandidate(StableId($"http-call|{project.Id}|{relative}|{Line(invocation)}|{method}|{route}"),
                        project.Id, project.Name, FindRepositoryPath(file, root, repositories), relative, Line(invocation),
                        method, route, symbol);
                }
            }
            else
            {
                var content = File.ReadAllText(file);
                foreach (Match match in TypeScriptHttpCallPattern.Matches(content))
                {
                    var rawRoute = ResolveTypeScriptUrlVariables(content, match.Groups["url"].Value);
                    if (!TryNormalizeRouteString(rawRoute, out var route)) continue;
                    var method = match.Groups["verb"].Success ? match.Groups["verb"].Value.ToUpperInvariant() : "GET";
                    if (match.Groups["fetch"].Success)
                    {
                        var section = content.Substring(match.Index, Math.Min(700, content.Length - match.Index));
                        var methodMatch = Regex.Match(section, """\bmethod\s*:\s*['"](?<method>GET|POST|PUT|PATCH|DELETE|HEAD|OPTIONS)['"]""", RegexOptions.IgnoreCase);
                        if (methodMatch.Success) method = methodMatch.Groups["method"].Value.ToUpperInvariant();
                    }
                    var line = 1 + content[..match.Index].Count(c => c == '\n');
                    var relativePath = Relative(root, file);
                    yield return new HttpCallCandidate(StableId($"http-call|{project.Id}|{relativePath}|{line}|{method}|{route}"),
                        project.Id, project.Name, FindRepositoryPath(file, root, repositories), relativePath, line,
                        method, route, match.Groups["fetch"].Success ? "fetch" : $"{match.Groups["verb"].Value.ToLowerInvariant()} HTTP client");
                }
            }
        }
    }

    private static readonly Regex TypeScriptHttpCallPattern = new(
        """(?:(?<fetch>\bfetch)|(?:\b(?:axios|api|client|httpClient|apiClient)\.(?<verb>get|post|put|patch|delete)))\s*\(\s*(?<quote>['"`])(?<url>[^'"`]+)\k<quote>""",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static bool TryGetCSharpHttpCall(InvocationExpressionSyntax invocation, SyntaxNode syntaxRoot,
        out string method, out string route)
    {
        method = ""; route = "";
        if (invocation.Expression is not MemberAccessExpressionSyntax) return false;
        var name = InvokedName(invocation.Expression);
        var arguments = invocation.ArgumentList.Arguments;
        ExpressionSyntax? urlExpression = null;
        switch (name)
        {
            case "GetAsync": case "GetFromJsonAsync": case "GetStringAsync": case "GetByteArrayAsync": case "GetStreamAsync":
                method = "GET"; urlExpression = arguments.FirstOrDefault()?.Expression; break;
            case "PostAsync": case "PostAsJsonAsync":
                method = "POST"; urlExpression = arguments.FirstOrDefault()?.Expression; break;
            case "PutAsync": case "PutAsJsonAsync":
                method = "PUT"; urlExpression = arguments.FirstOrDefault()?.Expression; break;
            case "PatchAsync": case "PatchAsJsonAsync":
                method = "PATCH"; urlExpression = arguments.FirstOrDefault()?.Expression; break;
            case "DeleteAsync":
                method = "DELETE"; urlExpression = arguments.FirstOrDefault()?.Expression; break;
            case "SendAsync": case "Send":
                if (arguments.Count == 0 || !TryExtractHttpRequestMessage(arguments[0].Expression, invocation, syntaxRoot, out method, out urlExpression)) return false;
                break;
            default: return false;
        }
        if (urlExpression is null) return false;
        var raw = StaticRouteText(urlExpression, invocation, syntaxRoot, new HashSet<string>(StringComparer.Ordinal));
        return raw is not null && TryNormalizeRouteString(raw, out route);
    }

    private static bool TryExtractHttpRequestMessage(ExpressionSyntax expression, InvocationExpressionSyntax invocation,
        SyntaxNode syntaxRoot, out string method, out ExpressionSyntax? urlExpression)
    {
        method = ""; urlExpression = null;
        ObjectCreationExpressionSyntax? creation = expression as ObjectCreationExpressionSyntax;
        if (expression is IdentifierNameSyntax identifier)
        {
            var scope = invocation.Ancestors().FirstOrDefault(node => node is BaseMethodDeclarationSyntax or LocalFunctionStatementSyntax
                or AnonymousFunctionExpressionSyntax) ?? syntaxRoot;
            creation = scope.DescendantNodesAndSelf().OfType<VariableDeclaratorSyntax>()
                .Where(variable => variable.Identifier.ValueText == identifier.Identifier.ValueText)
                .Select(variable => variable.Initializer?.Value).OfType<ObjectCreationExpressionSyntax>()
                .FirstOrDefault(candidate => candidate.Type.ToString().EndsWith("HttpRequestMessage", StringComparison.Ordinal));
        }
        if (creation is null || !creation.Type.ToString().EndsWith("HttpRequestMessage", StringComparison.Ordinal)) return false;
        var args = creation.ArgumentList?.Arguments;
        if (args is null || args.Value.Count < 2) return false;
        method = args.Value[0].Expression.ToString().Split('.').Last().Trim();
        if (method.StartsWith('"') && method.EndsWith('"')) method = method.Trim('"');
        method = method.ToUpperInvariant();
        if (!HttpMethodNames.IsAllowed(method)) return false;
        urlExpression = args.Value[1].Expression;
        return true;
    }

    private static string? StaticRouteText(ExpressionSyntax expression, InvocationExpressionSyntax invocation,
        SyntaxNode syntaxRoot, HashSet<string> visitedVariables)
    {
        switch (expression)
        {
            case ParenthesizedExpressionSyntax parenthesized:
                return StaticRouteText(parenthesized.Expression, invocation, syntaxRoot, visitedVariables);
            case LiteralExpressionSyntax literal when literal.IsKind(SyntaxKind.StringLiteralExpression):
                return literal.Token.ValueText;
            case InterpolatedStringExpressionSyntax interpolated:
            {
                var builder = new StringBuilder();
                foreach (var content in interpolated.Contents)
                {
                    if (content is InterpolatedStringTextSyntax text) builder.Append(text.TextToken.ValueText);
                    else if (content is InterpolationSyntax interpolation)
                        builder.Append('{').Append(interpolation.Expression.ToString().Split('.').Last()).Append('}');
                }
                return builder.ToString();
            }
            case ObjectCreationExpressionSyntax creation when creation.Type.ToString().EndsWith("Uri", StringComparison.Ordinal):
                return creation.ArgumentList?.Arguments.FirstOrDefault()?.Expression is { } uriArg
                    ? StaticRouteText(uriArg, invocation, syntaxRoot, visitedVariables) : null;
            case IdentifierNameSyntax identifier when visitedVariables.Add(identifier.Identifier.ValueText):
            {
                var scope = invocation.Ancestors().FirstOrDefault(node => node is BaseMethodDeclarationSyntax or LocalFunctionStatementSyntax
                    or AnonymousFunctionExpressionSyntax) ?? syntaxRoot;
                var declarator = scope.DescendantNodesAndSelf().OfType<VariableDeclaratorSyntax>()
                    .LastOrDefault(variable => variable.Identifier.ValueText == identifier.Identifier.ValueText && variable.Initializer is not null);
                return declarator?.Initializer is null ? null
                    : StaticRouteText(declarator.Initializer.Value, invocation, syntaxRoot, visitedVariables);
            }
            case BinaryExpressionSyntax binary when binary.IsKind(SyntaxKind.AddExpression):
            {
                var left = StaticRouteText(binary.Left, invocation, syntaxRoot, new HashSet<string>(visitedVariables, StringComparer.Ordinal));
                var right = StaticRouteText(binary.Right, invocation, syntaxRoot, new HashSet<string>(visitedVariables, StringComparer.Ordinal));
                var leftLooksLikeRoute = left is not null && (left.StartsWith('/') || left.Contains("/api/", StringComparison.OrdinalIgnoreCase)
                    || left.Contains("http://", StringComparison.OrdinalIgnoreCase) || left.Contains("https://", StringComparison.OrdinalIgnoreCase));
                var rightLooksLikeRoute = right is not null && (right.StartsWith('/') || right.Contains("/api/", StringComparison.OrdinalIgnoreCase)
                    || right.Contains("http://", StringComparison.OrdinalIgnoreCase) || right.Contains("https://", StringComparison.OrdinalIgnoreCase));
                if (left is not null && leftLooksLikeRoute && right is not null) return left.TrimEnd('/') + "/" + right.TrimStart('/');
                if (rightLooksLikeRoute) return right;
                if (left is not null && leftLooksLikeRoute && right is null && binary.Right is IdentifierNameSyntax id) return left.TrimEnd('/') + "/{" + id.Identifier.ValueText + "}";
                return leftLooksLikeRoute ? left : rightLooksLikeRoute ? right : null;
            }
            default: return null;
        }
    }

    private static string ResolveTypeScriptUrlVariables(string source, string rawRoute)
    {
        return Regex.Replace(rawRoute, @"\$\{\s*([A-Za-z_$][A-Za-z0-9_$]*)\s*\}", match =>
        {
            var identifier = Regex.Escape(match.Groups[1].Value);
            var declarationPattern = "(?:const|let|var)\\s+" + identifier + "\\s*=\\s*['\"](?<value>[^'\"]+)['\"]";
            var declaration = Regex.Match(source, declarationPattern, RegexOptions.Multiline);
            return declaration.Success ? declaration.Groups["value"].Value : "{" + match.Groups[1].Value + "}";
        });
    }

    private static bool TryNormalizeRouteString(string raw, out string route)
    {
        route = "";
        var value = raw.Trim();
        if (value.Length == 0) return false;
        if ((value.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || value.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            && value.Contains('{'))
        {
            var slash = value.IndexOf('/', value.IndexOf("://", StringComparison.Ordinal) + 3);
            value = slash >= 0 ? value[slash..] : "/";
        }
        else if (Uri.TryCreate(value, UriKind.Absolute, out var absolute) && absolute.Scheme is "http" or "https")
            value = absolute.AbsolutePath;
        else
        {
            var schemeAt = value.IndexOf("://", StringComparison.Ordinal);
            if (schemeAt >= 0)
            {
                var slash = value.IndexOf('/', schemeAt + 3);
                value = slash >= 0 ? value[slash..] : "/";
            }
        }
        if (value.Contains(' ') && !value.StartsWith('/')) return false;
        var query = value.IndexOfAny(new[] { '?', '#' });
        if (query >= 0) value = value[..query];
        value = Regex.Replace(value, @"\$\{([^}]+)\}", "{$1}");
        if (!value.StartsWith('/') && !value.Contains('/')) return false;
        if (!value.StartsWith('/') && value.Contains('/')) value = "/" + value;
        route = Regex.Replace(value, "/{2,}", "/");
        return route.Length > 0;
    }
}
