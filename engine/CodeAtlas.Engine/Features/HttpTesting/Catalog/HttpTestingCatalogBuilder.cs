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
    private static readonly HashSet<string> Excluded = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".hg", ".svn", "node_modules", "bin", "obj", ".next", "dist", "build", "coverage",
        ".turbo", ".vercel", "vendor", "Pods", "DerivedData", "TestResults"
    };
    private static readonly HashSet<string> Methods = new(StringComparer.OrdinalIgnoreCase)
        { "GET", "POST", "PUT", "PATCH", "DELETE", "HEAD", "OPTIONS", "TRACE" };

    public static HttpTestingCatalog Build(string rootPath, ProjectNode[] projects)
    {
        var root = Path.GetFullPath(rootPath);
        var repositories = GitWorkspaceInspector.Scan(root);
        var warnings = new List<string>();
        var sourceFiles = EnumerateFiles(root).Where(IsSource).Take(30_000).ToArray();
        var typeIndex = BuildTypeIndex(sourceFiles);
        var endpoints = new List<ApiEndpoint>();
        var filesByProject = new Dictionary<string, string[]>(StringComparer.Ordinal);
        foreach (var project in projects)
        {
            var projectEntry = Path.GetFullPath(Path.Combine(root, project.Path.Replace('/', Path.DirectorySeparatorChar)));
            var projectDir = Directory.Exists(projectEntry) ? projectEntry : Path.GetDirectoryName(projectEntry) ?? root;
            var files = sourceFiles.Where(file => IsWithinOrSame(file, projectDir))
                .Where(file => !IsWithinNestedProject(file, projectDir, project, projects, root)).ToArray();
            filesByProject[project.Id] = files;
            if (project.Kind == "Dotnet")
                endpoints.AddRange(ScanDotnet(project, files, root, repositories, typeIndex));
            else if (project.Kind == "Next.js")
                endpoints.AddRange(ScanNext(project, projectDir, files, root, repositories));
        }
        endpoints = endpoints.GroupBy(e => e.Id, StringComparer.Ordinal).Select(g => g.First())
            .OrderBy(e => e.RepositoryPath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(e => e.ProjectName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(e => e.Route, StringComparer.OrdinalIgnoreCase).ToList();

        var httpFiles = EnumerateFiles(root).Where(path =>
        {
            var extension = Path.GetExtension(path);
            return extension.Equals(".http", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".rest", StringComparison.OrdinalIgnoreCase);
        }).Take(5_000).ToArray();
        var requests = new List<HttpTestRequest>();
        foreach (var file in httpFiles)
        {
            try
            {
                var relativeFile = Relative(root, file);
                var parsedRequests = HttpFileParser.Parse(file);
                var repositoryPath = FindRepositoryPath(file, root, repositories);
                var owner = FindOwningProject(file, projects, root);
                foreach (var parsed in parsedRequests)
                {
                    var matchPath = HttpFileParser.PathForMatch(parsed.Url, parsed.FileVariables);
                    var rawMatches = endpoints.Where(e =>
                            (e.Method.Equals(parsed.Method, StringComparison.OrdinalIgnoreCase) || e.Method == "ANY")
                            && RouteMatches(e.Route, matchPath))
                        .ToArray();
                    // Prefer the project that owns the test file, then the same Git repository.
                    // Only fall back to workspace-wide matching when neither can disambiguate it.
                    var ownerMatches = owner is null ? Array.Empty<ApiEndpoint>() : rawMatches.Where(e => e.ProjectId == owner.Id).ToArray();
                    var sameRepoMatches = rawMatches.Where(e => e.RepositoryPath.Equals(repositoryPath, StringComparison.OrdinalIgnoreCase)).ToArray();
                    var preferredMatches = ownerMatches.Length > 0 ? ownerMatches
                        : sameRepoMatches.Length > 0 ? sameRepoMatches
                        : rawMatches;
                    var matched = preferredMatches.Select(e => e.Id).Distinct(StringComparer.Ordinal).ToArray();
                    var requestOwner = matched.Length == 1
                        ? projects.FirstOrDefault(p => p.Id == preferredMatches.First(e => e.Id == matched[0]).ProjectId)
                        : owner;
                    var envInfo = HttpEnvironmentLoader.Inspect(root, Path.GetDirectoryName(file)!);
                    var knownVariables = new HashSet<string>(parsed.FileVariables.Keys, StringComparer.OrdinalIgnoreCase);
                    knownVariables.UnionWith(envInfo.AllVariableNames);
                    var unresolved = Regex.Matches(parsed.Url + "\n" + parsed.HeadersText + "\n" + parsed.Body,
                            @"\{\{\s*([A-Za-z0-9_.-]+)\s*\}\}")
                        .Select(m => m.Groups[1].Value).Where(name => !knownVariables.Contains(name))
                        .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                    requests.Add(new HttpTestRequest(
                        StableId($"http|{relativeFile}|{parsed.Index}"), requestOwner?.Id ?? "",
                        requestOwner?.Name ?? "Sin proyecto asociado", repositoryPath, relativeFile, parsed.Index,
                        parsed.Name, parsed.Method, parsed.Url, parsed.HeadersText, parsed.Body, parsed.Line,
                        matched, unresolved, envInfo.EnvironmentNames));
                }
            }
            catch (Exception ex) { warnings.Add($"No se pudo analizar {Relative(root, file)}: {ex.Message}"); }
        }
        var outgoingCalls = projects.SelectMany(project =>
        {
            filesByProject.TryGetValue(project.Id, out var projectFiles);
            return ScanOutgoingHttpCalls(project, projectFiles ?? Array.Empty<string>(), root, repositories);
        }).ToArray();
        var linkedCalls = outgoingCalls.Select(call =>
        {
            var candidates = endpoints.Where(endpoint => endpoint.ProjectId != call.SourceProjectId
                    && (endpoint.Method.Equals(call.Method, StringComparison.OrdinalIgnoreCase) || endpoint.Method == "ANY")
                    && RouteMatches(endpoint.Route, call.Route))
                .ToArray();
            return new HttpCallTrace(call.Id, call.SourceProjectId, call.SourceProjectName, call.RepositoryPath,
                call.SourcePath, call.Line, call.Method, call.Route, call.Symbol,
                candidates.Select(candidate => candidate.Id).Distinct(StringComparer.Ordinal).ToArray(),
                candidates.Length == 1 ? "inferred" : candidates.Length > 1 ? "ambiguous" : "unresolved",
                candidates.Length == 1 ? "Coincidencia de método y ruta con un endpoint de otro proyecto; no demuestra tráfico real."
                    : candidates.Length > 1 ? "Varios endpoints de otros proyectos coinciden con el método y la ruta; confirma el destino mediante BaseAddress/configuración."
                    : "Se detectó una llamada HTTP con ruta estática, pero no se encontró un endpoint receptor único.");
        }).OrderBy(call => call.RepositoryPath, StringComparer.OrdinalIgnoreCase)
          .ThenBy(call => call.SourceProjectName, StringComparer.OrdinalIgnoreCase)
          .ThenBy(call => call.SourcePath, StringComparer.OrdinalIgnoreCase).ToArray();
        if (endpoints.Count == 0) warnings.Add("No se detectaron endpoints con rutas estáticas. Revisa rutas dinámicas, grupos complejos o configuración externa.");
        if (httpFiles.Length == 0) warnings.Add("No se encontraron archivos .http o .rest. Puedes probar endpoints descubiertos o generar un archivo .http desde su esquema.");
        var unmatched = requests.Count(r => r.AssociatedEndpointIds.Length == 0);
        if (unmatched > 0) warnings.Add($"{unmatched} petición(es) .http no se pudieron asociar automáticamente; siguen disponibles para ejecución manual.");
        if (linkedCalls.Any(call => call.Confidence == "ambiguous")) warnings.Add("Algunas llamadas HTTP coinciden con varios endpoints; revisa la BaseAddress, el host y la configuración del cliente para confirmar el destino.");
        return new HttpTestingCatalog(root, endpoints.ToArray(), requests.ToArray(), linkedCalls, warnings.Distinct().ToArray());
    }

    private static IEnumerable<ApiEndpoint> ScanDotnet(ProjectNode project, string[] files, string root,
        GitRepositoryState[] repositories, Dictionary<string, BaseTypeDeclarationSyntax[]> types)
    {
        foreach (var file in files.Where(f => Path.GetExtension(f).Equals(".cs", StringComparison.OrdinalIgnoreCase)))
        {
            SyntaxTree tree;
            try { tree = CSharpSyntaxTree.ParseText(File.ReadAllText(file), new CSharpParseOptions(LanguageVersion.Preview), file); }
            catch { continue; }
            var syntaxRoot = tree.GetRoot();
            var relative = Relative(root, file);
            foreach (var controller in syntaxRoot.DescendantNodes().OfType<ClassDeclarationSyntax>())
            {
                var controllerRouteTemplate = GetAttributeRoute(controller.AttributeLists, "Route") ?? "";
                foreach (var method in controller.Members.OfType<MethodDeclarationSyntax>())
                {
                    var httpAttributes = method.AttributeLists.SelectMany(list => list.Attributes)
                        .Where(attribute => TryHttpVerb(AttributeName(attribute), out _)).ToArray();
                    foreach (var attribute in httpAttributes)
                    {
                        if (!TryHttpVerb(AttributeName(attribute), out var verb)) continue;
                        var actionRoute = GetAttributeString(attribute) ?? GetAttributeRoute(method.AttributeLists, "Route") ?? "";
                        var controllerName = controller.Identifier.ValueText.EndsWith("Controller", StringComparison.OrdinalIgnoreCase)
                            ? controller.Identifier.ValueText[..^10] : controller.Identifier.ValueText;
                        var controllerRoute = controllerRouteTemplate.Replace("[controller]", controllerName, StringComparison.OrdinalIgnoreCase)
                            .Replace("[action]", method.Identifier.ValueText, StringComparison.OrdinalIgnoreCase);
                        actionRoute = actionRoute.Replace("[controller]", controllerName, StringComparison.OrdinalIgnoreCase)
                            .Replace("[action]", method.Identifier.ValueText, StringComparison.OrdinalIgnoreCase);
                        var route = CombineRoute(controllerRoute, actionRoute);
                        var bodyType = FindBodyType(method.ParameterList.Parameters, types);
                        yield return MakeEndpoint(project, root, repositories, "ASP.NET Core Controller", verb, route,
                            relative, Line(method), bodyType, types, project.ListenAddresses,
                            $"Atributo {AttributeName(attribute)} en {controller.Identifier.ValueText}.{method.Identifier.ValueText}.");
                    }
                }
            }

            // FastEndpoints uses an Endpoint<TRequest,TResponse> base class and declares
            // the HTTP verb + route inside Configure(). These are not ASP.NET attributes
            // or Minimal API MapGet calls, so they need an explicit detector.
            foreach (var endpoint in ScanFastEndpoints(project, syntaxRoot, root, repositories, types))
                yield return endpoint;

            var groupDefinitions = syntaxRoot.DescendantNodes().OfType<VariableDeclaratorSyntax>()
                .Where(variable => variable.Initializer?.Value is InvocationExpressionSyntax)
                .Select(variable => (Name: variable.Identifier.ValueText, Invocation: (InvocationExpressionSyntax)variable.Initializer!.Value))
                .Where(variable => InvokedName(variable.Invocation.Expression) == "MapGroup")
                .GroupBy(variable => variable.Name, StringComparer.Ordinal)
                .Where(group => group.Count() == 1)
                .Select(group => group.Single()).ToArray();
            var groups = new Dictionary<string, string>(StringComparer.Ordinal);
            // Resolve nested groups over several passes: api.MapGroup("/v1") followed by
            // v1.MapGroup("/cargo") must produce /v1/cargo, regardless of declaration nesting.
            for (var pass = 0; pass < groupDefinitions.Length; pass++)
            {
                var changed = false;
                foreach (var definition in groupDefinitions)
                {
                    var localPrefix = FirstString(definition.Invocation.ArgumentList);
                    if (string.IsNullOrWhiteSpace(localPrefix) || groups.ContainsKey(definition.Name)) continue;
                    var parentPrefix = definition.Invocation.Expression is MemberAccessExpressionSyntax groupMember
                        && groupMember.Expression is IdentifierNameSyntax parentIdentifier
                        && groups.TryGetValue(parentIdentifier.Identifier.ValueText, out var foundParent)
                            ? foundParent : string.Empty;
                    groups[definition.Name] = CombineRoute(parentPrefix, localPrefix);
                    changed = true;
                }
                if (!changed) break;
            }

            foreach (var invocation in syntaxRoot.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                var name = InvokedName(invocation.Expression);
                if (TryMapVerb(name, out var verb))
                {
                    var route = FirstString(invocation.ArgumentList);
                    if (route is null) continue;
                    if (invocation.Expression is MemberAccessExpressionSyntax member
                        && member.Expression is IdentifierNameSyntax receiver
                        && groups.TryGetValue(receiver.Identifier.ValueText, out var prefix)) route = CombineRoute(prefix, route);
                    var callback = invocation.ArgumentList.Arguments.Skip(1).Select(a => a.Expression).OfType<LambdaExpressionSyntax>().FirstOrDefault();
                    var parameters = callback switch
                    {
                        ParenthesizedLambdaExpressionSyntax parenthesized => parenthesized.ParameterList.Parameters,
                        SimpleLambdaExpressionSyntax simple => SyntaxFactory.SeparatedList(new[] { simple.Parameter }),
                        _ => default
                    };
                    var bodyType = FindBodyType(parameters, types);
                    yield return MakeEndpoint(project, root, repositories, "ASP.NET Core Minimal API", verb, route,
                        relative, Line(invocation), bodyType, types, project.ListenAddresses, "Ruta literal de Minimal API.");
                }
                else if (name == "MapMethods")
                {
                    var route = FirstString(invocation.ArgumentList);
                    if (route is null) continue;
                    var methodExpression = invocation.ArgumentList.Arguments.Skip(1).FirstOrDefault()?.Expression;
                    var methods = methodExpression is null ? Array.Empty<string>() : methodExpression.DescendantNodesAndSelf()
                        .OfType<LiteralExpressionSyntax>()
                        .Where(l => l.IsKind(SyntaxKind.StringLiteralExpression)).Select(l => l.Token.ValueText)
                        .Where(Methods.Contains).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                    foreach (var method in methods)
                        yield return MakeEndpoint(project, root, repositories, "ASP.NET Core Minimal API", method, route,
                            relative, Line(invocation), null, types, project.ListenAddresses, "MapMethods con verbos HTTP literales.");
                }
            }
        }
    }

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
