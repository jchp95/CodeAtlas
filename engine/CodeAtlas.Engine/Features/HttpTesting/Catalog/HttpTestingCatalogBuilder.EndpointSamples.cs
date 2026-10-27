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
private static ApiEndpoint MakeEndpoint(ProjectNode project, string root, GitRepositoryState[] repositories,
        string framework, string method, string route, string file, int line, string? requestType,
        Dictionary<string, BaseTypeDeclarationSyntax[]> types, string[] listenAddresses, string detail)
    {
        route = NormalizeRoute(route);
        var sample = requestType is null ? null : BuildSampleBody(requestType, types);
        var schemaSource = requestType is null ? "none" : sample is null ? "type-unresolved" : "source-type";
        var pathParams = Regex.Matches(route, @"\{\*?([A-Za-z_][A-Za-z0-9_]*)[^}]*\}|\[([A-Za-z_][A-Za-z0-9_]*)\]|:([A-Za-z_][A-Za-z0-9_]*)")
            .Cast<Match>().Select(m => m.Groups.Cast<Group>().Skip(1).First(g => g.Success).Value)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var fullFile = Path.Combine(root, file.Replace('/', Path.DirectorySeparatorChar));
        return new ApiEndpoint(StableId($"endpoint|{project.Id}|{method}|{route}|{file}|{line}"), project.Id, project.Name,
            FindRepositoryPath(fullFile, root, repositories), framework, method.ToUpperInvariant(), route, file, line,
            requestType, sample, schemaSource, pathParams, "source", detail, listenAddresses);
    }

    private static Dictionary<string, BaseTypeDeclarationSyntax[]> BuildTypeIndex(string[] files)
    {
        var index = new Dictionary<string, List<BaseTypeDeclarationSyntax>>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files.Where(f => Path.GetExtension(f).Equals(".cs", StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                var syntax = CSharpSyntaxTree.ParseText(File.ReadAllText(file), new CSharpParseOptions(LanguageVersion.Preview)).GetRoot();
                foreach (var declaration in syntax.DescendantNodes().OfType<BaseTypeDeclarationSyntax>())
                {
                    var name = declaration.Identifier.ValueText;
                    if (!index.TryGetValue(name, out var list)) index[name] = list = new List<BaseTypeDeclarationSyntax>();
                    list.Add(declaration);
                }
            }
            catch { /* Partial/generated files are best effort. */ }
        }
        return index.ToDictionary(pair => pair.Key, pair => pair.Value.ToArray(), StringComparer.OrdinalIgnoreCase);
    }

    private static string? FindBodyType(SeparatedSyntaxList<ParameterSyntax> parameters, Dictionary<string, BaseTypeDeclarationSyntax[]> types)
    {
        var parametersList = parameters.ToArray();
        var fromBody = parametersList.FirstOrDefault(p => p.AttributeLists.SelectMany(a => a.Attributes).Any(a => AttributeName(a) == "FromBody"));
        if (fromBody?.Type is not null) return fromBody.Type.ToString();
        foreach (var parameter in parametersList)
        {
            if (parameter.Type is null) continue;
            var attributes = parameter.AttributeLists.SelectMany(a => a.Attributes).Select(AttributeName).ToArray();
            if (attributes.Any(a => new[] { "FromServices", "FromRoute", "FromQuery", "FromHeader", "FromForm" }.Contains(a))) continue;
            var type = parameter.Type.ToString().TrimEnd('?');
            var shortName = type.Split('.').Last();
            if (new[] { "CancellationToken", "HttpContext", "HttpRequest", "HttpResponse", "ClaimsPrincipal", "ISender", "IMediator", "IPublishEndpoint", "IBus", "IServiceProvider", "IFormFile" }.Contains(shortName)) continue;
            if (types.ContainsKey(shortName)) return type;
        }
        return null;
    }

    private static string? BuildSampleBody(string type, Dictionary<string, BaseTypeDeclarationSyntax[]> types)
    {
        var sample = SampleValue(type, types, 0, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        return sample is null ? null : JsonSerializer.Serialize(sample, new JsonSerializerOptions { WriteIndented = true });
    }

    private static object? SampleValue(string rawType, Dictionary<string, BaseTypeDeclarationSyntax[]> types, int depth, HashSet<string> stack)
    {
        if (depth > 3) return new Dictionary<string, object?>();
        var type = Regex.Replace(rawType.Replace("global::", "", StringComparison.Ordinal), @"[?\s]", "");
        if (type.EndsWith("[]")) return new object?[] { SampleValue(type[..^2], types, depth + 1, stack) };
        var genericStart = type.IndexOf('<');
        if (genericStart > 0 && type.EndsWith('>'))
        {
            var genericName = type[..genericStart].Split('.').Last();
            var inner = type[(genericStart + 1)..^1];
            if (new[] { "List", "IList", "IReadOnlyList", "IEnumerable", "ICollection", "IReadOnlyCollection" }.Contains(genericName))
                return new object?[] { SampleValue(inner, types, depth + 1, stack) };
            if (genericName == "Nullable") return SampleValue(inner, types, depth + 1, stack);
            return new Dictionary<string, object?>();
        }
        var simple = type.Split('.').Last();
        if (simple is "string" or "String") return "string";
        if (simple is "Guid") return "00000000-0000-0000-0000-000000000000";
        if (simple is "DateTime" or "DateTimeOffset") return "2026-01-01T00:00:00Z";
        if (simple is "TimeSpan") return "00:00:00";
        if (simple is "bool" or "Boolean") return true;
        if (new[] { "byte", "sbyte", "short", "ushort", "int", "Int32", "uint", "long", "Int64", "ulong" }.Contains(simple)) return 0;
        if (new[] { "float", "double", "decimal", "Single", "Double", "Decimal" }.Contains(simple)) return 0.0;
        if (simple is "object" or "Object") return new Dictionary<string, object?>();
        if (!types.TryGetValue(simple, out var candidates) || candidates.Length == 0) return null;
        BaseTypeDeclarationSyntax? declaration;
        if (type.Contains('.', StringComparison.Ordinal))
        {
            declaration = candidates.FirstOrDefault(c => FullTypeName(c).Equals(type, StringComparison.Ordinal)
                || FullTypeName(c).EndsWith("." + type, StringComparison.Ordinal));
            if (declaration is null) return null;
        }
        else
        {
            // Never invent a schema when a short type name maps to multiple source declarations.
            if (candidates.Length != 1) return null;
            declaration = candidates[0];
        }
        if (declaration is EnumDeclarationSyntax enumDeclaration) return enumDeclaration.Members.FirstOrDefault()?.Identifier.ValueText ?? "Value";
        if (!stack.Add(simple)) return new Dictionary<string, object?>();
        try
        {
            var result = new Dictionary<string, object?>(StringComparer.Ordinal);
            var parameters = declaration switch
            {
                ClassDeclarationSyntax c => c.ParameterList?.Parameters,
                StructDeclarationSyntax s => s.ParameterList?.Parameters,
                RecordDeclarationSyntax r => r.ParameterList?.Parameters,
                _ => null
            };
            if (parameters is { } parameterList)
                foreach (var parameter in parameterList.Where(p => p.Type is not null))
                    result[JsonName(parameter.Identifier.ValueText)] = SampleValue(parameter.Type!.ToString(), types, depth + 1, stack);
            if (declaration is TypeDeclarationSyntax typeDeclaration)
            {
                foreach (var property in typeDeclaration.Members.OfType<PropertyDeclarationSyntax>())
                {
                    var propertyName = property.AttributeLists.SelectMany(a => a.Attributes)
                        .Where(a => AttributeName(a) == "JsonPropertyName")
                        .Select(a => a.ArgumentList?.Arguments.FirstOrDefault()?.Expression).OfType<LiteralExpressionSyntax>()
                        .FirstOrDefault(l => l.IsKind(SyntaxKind.StringLiteralExpression))?.Token.ValueText
                        ?? JsonName(property.Identifier.ValueText);
                    if (property.Type is not null && !result.ContainsKey(propertyName))
                        result[propertyName] = SampleValue(property.Type.ToString(), types, depth + 1, stack);
                }
            }
            return result;
        }
        finally { stack.Remove(simple); }
    }

    private static string FullTypeName(BaseTypeDeclarationSyntax declaration)
    {
        var ns = string.Join('.', declaration.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().Select(n => n.Name.ToString()).Reverse());
        return ns.Length == 0 ? declaration.Identifier.ValueText : ns + "." + declaration.Identifier.ValueText;
    }
    private static string JsonName(string value) => JsonNamingPolicy.CamelCase.ConvertName(value);
}
