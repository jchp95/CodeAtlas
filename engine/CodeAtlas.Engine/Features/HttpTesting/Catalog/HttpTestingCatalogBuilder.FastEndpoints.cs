using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

static partial class HttpTestingCatalogBuilder
{
    private static readonly Dictionary<string, string> FastEndpointVerbs = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Get"] = "GET", ["Post"] = "POST", ["Put"] = "PUT", ["Patch"] = "PATCH",
        ["Delete"] = "DELETE", ["Head"] = "HEAD", ["Options"] = "OPTIONS"
    };

    private static IEnumerable<ApiEndpoint> ScanFastEndpoints(
        ProjectNode project,
        SyntaxNode syntaxRoot,
        string root,
        GitRepositoryState[] repositories,
        Dictionary<string, BaseTypeDeclarationSyntax[]> types)
    {
        foreach (var endpointClass in syntaxRoot.DescendantNodes().OfType<ClassDeclarationSyntax>())
        {
            if (endpointClass.BaseList is null) continue;
            var endpointBase = endpointClass.BaseList.Types
                .Select(baseType => baseType.Type.DescendantNodesAndSelf().OfType<GenericNameSyntax>().LastOrDefault())
                .FirstOrDefault(generic => generic is not null &&
                    (generic.Identifier.ValueText.Equals("Endpoint", StringComparison.Ordinal)
                     || generic.Identifier.ValueText.Equals("EndpointWithoutRequest", StringComparison.Ordinal)
                     || generic.Identifier.ValueText.Equals("EndpointWithRequest", StringComparison.Ordinal)));
            if (endpointBase is null) continue;

            var configure = endpointClass.Members.OfType<MethodDeclarationSyntax>()
                .FirstOrDefault(method => method.Identifier.ValueText.Equals("Configure", StringComparison.Ordinal));
            if (configure is null) continue;

            var requestType = endpointBase.Identifier.ValueText switch
            {
                "Endpoint" when endpointBase.TypeArgumentList.Arguments.Count >= 1 => endpointBase.TypeArgumentList.Arguments[0].ToString(),
                "EndpointWithRequest" when endpointBase.TypeArgumentList.Arguments.Count >= 1 => endpointBase.TypeArgumentList.Arguments[0].ToString(),
                _ => null
            };
            var routesFromRouteCalls = configure.DescendantNodes().OfType<InvocationExpressionSyntax>()
                .Where(invocation => InvokedName(invocation.Expression) is "Route" or "Routes")
                .SelectMany(invocation => invocation.ArgumentList.Arguments.Select(argument => argument.Expression)
                    .OfType<LiteralExpressionSyntax>()
                    .Where(literal => literal.IsKind(SyntaxKind.StringLiteralExpression))
                    .Select(literal => literal.Token.ValueText))
                .Where(route => !string.IsNullOrWhiteSpace(route)).Distinct(StringComparer.Ordinal).ToArray();
            var verbsFromVerbsCall = configure.DescendantNodes().OfType<InvocationExpressionSyntax>()
                .Where(invocation => InvokedName(invocation.Expression).Equals("Verbs", StringComparison.Ordinal))
                .SelectMany(invocation => invocation.ArgumentList.Arguments)
                .Select(argument => argument.Expression.ToString().Split('.').LastOrDefault() ?? string.Empty)
                .Select(value => value.StartsWith("Http", StringComparison.OrdinalIgnoreCase) ? value[4..] : value)
                .Where(value => new[] { "GET", "POST", "PUT", "PATCH", "DELETE", "HEAD", "OPTIONS" }
                    .Contains(value, StringComparer.OrdinalIgnoreCase))
                .Select(value => value.ToUpperInvariant()).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

            var emittedDirectRoutes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var invocation in configure.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                var name = InvokedName(invocation.Expression);
                if (!FastEndpointVerbs.TryGetValue(name, out var verb)) continue;
                var route = FirstString(invocation.ArgumentList);
                if (string.IsNullOrWhiteSpace(route)) continue;
                emittedDirectRoutes.Add($"{verb}|{route}");
                yield return MakeEndpoint(project, root, repositories, "FastEndpoints", verb, route,
                    Relative(root, invocation.SyntaxTree.FilePath), Line(invocation), requestType, types,
                    project.ListenAddresses, $"FastEndpoints {endpointClass.Identifier.ValueText}.Configure(): {name}(\"{route}\").");
            }

            // Also support the declarative form Configure() { Verbs(Http.GET); Routes("/path"); }.
            if (routesFromRouteCalls.Length > 0 && verbsFromVerbsCall.Length > 0)
            {
                foreach (var verb in verbsFromVerbsCall)
                foreach (var route in routesFromRouteCalls)
                {
                    if (!emittedDirectRoutes.Add($"{verb}|{route}")) continue;
                    SyntaxNode sourceNode = configure.DescendantNodes().OfType<InvocationExpressionSyntax>()
                        .FirstOrDefault(invocation => InvokedName(invocation.Expression) is "Route" or "Routes") as SyntaxNode ?? configure;
                    yield return MakeEndpoint(project, root, repositories, "FastEndpoints", verb, route,
                        Relative(root, sourceNode.SyntaxTree.FilePath), Line(sourceNode), requestType, types,
                        project.ListenAddresses, $"FastEndpoints {endpointClass.Identifier.ValueText}.Configure(): Verbs/Routes.");
                }
            }
        }
    }
}
