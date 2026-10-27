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
private IReadOnlyDictionary<string, string[]> BuildRepositoryTypeIndex(List<string> warnings)
    {
        var found = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var sourceFiles = EnumerateFiles(root, "*.cs").Take(40_001).ToArray();
        if (sourceFiles.Length > 40_000)
        {
            warnings.Add("The repository-wide C# type index was limited to 40,000 source files; unresolved cross-project contract names may remain candidates.");
            sourceFiles = sourceFiles.Take(40_000).ToArray();
        }

        foreach (var file in sourceFiles)
        {
            try
            {
                var tree = CSharpSyntaxTree.ParseText(
                    SafeRead(file),
                    new CSharpParseOptions(LanguageVersion.Preview, DocumentationMode.Parse),
                    file);
                var syntaxRoot = tree.GetRoot();
                foreach (var declaration in syntaxRoot.DescendantNodes().OfType<BaseTypeDeclarationSyntax>())
                {
                    var simpleName = declaration.Identifier.ValueText;
                    if (string.IsNullOrWhiteSpace(simpleName)) continue;
                    var namespaceName = string.Join(".", declaration.Ancestors()
                        .OfType<BaseNamespaceDeclarationSyntax>()
                        .Select(namespaceDeclaration => namespaceDeclaration.Name.ToString())
                        .Reverse());
                    var fullName = string.IsNullOrWhiteSpace(namespaceName) ? simpleName : $"{namespaceName}.{simpleName}";
                    if (!found.TryGetValue(simpleName, out var names)) found[simpleName] = names = new HashSet<string>(StringComparer.Ordinal);
                    names.Add(fullName);
                }
            }
            catch (Exception ex)
            {
                warnings.Add($"Could not index C# declarations from {Relative(file)}: {ex.Message}");
            }
        }

        return found.ToDictionary(pair => pair.Key, pair => pair.Value.OrderBy(value => value, StringComparer.Ordinal).ToArray(), StringComparer.Ordinal);
    }

    private ProjectDefinition BuildDotnetProject(
        string projectFile,
        List<string> warnings,
        IReadOnlyDictionary<string, string[]> repositoryTypeIndex)
    {
        var relative = Relative(projectFile);
        var projectDirectory = Path.GetDirectoryName(projectFile)!;
        var content = SafeRead(projectFile);
        var name = Path.GetFileNameWithoutExtension(projectFile);
        var framework = DetectTargetFramework(content);
        var packages = new List<string>();
        var projectReferences = new List<string>();

        try
        {
            var document = XDocument.Parse(content);
            packages.AddRange(document.Descendants().Where(e => e.Name.LocalName == "PackageReference")
                .Select(e => (string?)e.Attribute("Include") ?? (string?)e.Attribute("Update") ?? (string?)e.Element(e.Name.Namespace + "Include"))
                .Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v!.Trim()));

            foreach (var element in document.Descendants().Where(e => e.Name.LocalName == "ProjectReference"))
            {
                var include = (string?)element.Attribute("Include");
                if (string.IsNullOrWhiteSpace(include) || include.Contains("$(", StringComparison.Ordinal)) continue;
                var absolute = Path.GetFullPath(Path.Combine(projectDirectory, include.Replace('\\', Path.DirectorySeparatorChar)));
                projectReferences.Add(Relative(absolute));
            }
        }
        catch (Exception ex)
        {
            warnings.Add($"Could not fully parse {relative}: {ex.Message}");
        }

        var sourceFiles = EnumerateFiles(projectDirectory, "*.cs").ToArray();
        var nestedProjectDirectories = EnumerateFiles(projectDirectory, "*.csproj")
            .Where(p => !PathTools.PathsEqual(p, projectFile))
            .Select(p => Path.GetDirectoryName(p)).Where(p => p is not null).Cast<string>().ToArray();
        sourceFiles = sourceFiles.Where(file => !nestedProjectDirectories.Any(dir => PathTools.IsWithin(file, dir))).Take(6000).ToArray();

        var endpoints = new List<string>();
        var published = new List<string>();
        var consumed = new List<string>();
        var publishedEvidence = new List<string>();
        var consumedEvidence = new List<string>();
        var codeEvidence = new List<ArchitectureEvidence>();
        var httpTargets = new List<HttpTargetCandidate>();
        var syntaxTrees = sourceFiles.Select(sourceFile =>
        {
            var parseOptions = new CSharpParseOptions(LanguageVersion.Preview, DocumentationMode.Parse);
            return CSharpSyntaxTree.ParseText(SafeRead(sourceFile), parseOptions, Relative(sourceFile));
        }).ToArray();

        CSharpCompilation compilation;
        try
        {
            compilation = CSharpCompilation.Create(
                $"CodeAtlas_{Regex.Replace(name, "[^A-Za-z0-9_]", "_")}",
                syntaxTrees,
                LoadMetadataReferences(projectDirectory, framework),
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
        }
        catch (Exception ex)
        {
            warnings.Add($"Semantic analysis could not initialize for {relative}: {ex.Message}");
            compilation = CSharpCompilation.Create($"CodeAtlas_{Guid.NewGuid():N}", syntaxTrees, PlatformMetadataReferences.Value,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        }

        foreach (var (sourceFile, syntaxTree) in sourceFiles.Zip(syntaxTrees))
        {
            var text = SafeRead(sourceFile);
            var relativeFile = Relative(sourceFile);
            var semanticModel = compilation.GetSemanticModel(syntaxTree, ignoreAccessibility: true);
            DetectDotnetEndpoints(syntaxTree, relativeFile, endpoints, codeEvidence);
            DetectMessaging(syntaxTree, semanticModel, relativeFile, published, consumed, publishedEvidence, consumedEvidence, codeEvidence);
            DetectDependencyInjection(syntaxTree, semanticModel, relativeFile, codeEvidence);
            if (!relativeFile.Split('/').Any(segment => segment.Contains("Test", StringComparison.OrdinalIgnoreCase)))
                DetectHttpTargets(text, relativeFile, httpTargets);
        }
        foreach (var configFile in EnumerateFiles(projectDirectory, "appsettings*.json"))
            DetectHttpTargets(SafeRead(configFile), Relative(configFile), httpTargets);
        var listenAddresses = FindListenAddresses(projectDirectory);

        var businessFlows = BusinessFlowAnalyzer.Analyze(root, relative, name, syntaxTrees, compilation, repositoryTypeIndex);
        var node = new ProjectNode(
            relative, name, relative, "Dotnet", framework,
            endpoints.Distinct().Take(150).ToArray(),
            packages.Distinct(StringComparer.OrdinalIgnoreCase).Take(100).OrderBy(x => x).ToArray(),
            projectReferences.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            NormalizeContractCandidates(published).ToArray(),
            NormalizeContractCandidates(consumed).ToArray(),
            publishedEvidence.Distinct().Take(100).ToArray(),
            consumedEvidence.Distinct().Take(100).ToArray(),
            httpTargets.GroupBy(candidate => $"{candidate.Url}|{candidate.SourcePath}", StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First()).Take(100).ToArray(),
            listenAddresses,
            codeEvidence.Distinct().Take(1200).ToArray());
        return new ProjectDefinition(node, projectFile, businessFlows);
    }

    private ProjectDefinition BuildNodeProject(string packageFile, List<string> warnings)
    {
        var relative = Relative(packageFile);
        var packageDirectory = Path.GetDirectoryName(packageFile)!;
        var content = SafeRead(packageFile);
        var name = Path.GetFileName(packageDirectory);
        var dependencies = new List<string>();
        string? framework = null;

        try
        {
            using var document = JsonDocument.Parse(content);
            var json = document.RootElement;
            if (json.TryGetProperty("name", out var nameElement) && nameElement.ValueKind == JsonValueKind.String)
                name = nameElement.GetString() ?? name;

            foreach (var propertyName in new[] { "dependencies", "devDependencies", "peerDependencies" })
            {
                if (json.TryGetProperty(propertyName, out var group) && group.ValueKind == JsonValueKind.Object)
                    dependencies.AddRange(group.EnumerateObject().Select(p => p.Name));
            }
            if (dependencies.Contains("next", StringComparer.OrdinalIgnoreCase)) framework = "Next.js";
            else if (dependencies.Contains("react", StringComparer.OrdinalIgnoreCase)) framework = "React";
            else if (dependencies.Contains("typescript", StringComparer.OrdinalIgnoreCase)) framework = "TypeScript";
        }
        catch (Exception ex)
        {
            warnings.Add($"Could not parse {relative}: {ex.Message}");
        }

        var isNext = dependencies.Contains("next", StringComparer.OrdinalIgnoreCase);
        var routeCandidates = isNext ? FindNextRouteCandidates(packageDirectory) : Array.Empty<string>();
        var httpTargets = new List<HttpTargetCandidate>();
        foreach (var sourceFile in EnumerateFiles(packageDirectory, "*.*").Where(IsSupportedSourceFile).Take(6000))
        {
            var relativeFile = Relative(sourceFile);
            if (relativeFile.Split('/').Any(segment => segment.Contains("Test", StringComparison.OrdinalIgnoreCase))) continue;
            DetectHttpTargets(SafeRead(sourceFile), relativeFile, httpTargets);
        }
        foreach (var envFile in EnumerateFiles(packageDirectory, ".env*"))
            DetectHttpTargets(SafeRead(envFile), Relative(envFile), httpTargets);

        var node = new ProjectNode(
            relative, name, relative, isNext ? "Next.js" : "Node.js", framework,
            routeCandidates,
            dependencies.Distinct(StringComparer.OrdinalIgnoreCase).Take(100).OrderBy(x => x).ToArray(),
            Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(),
            httpTargets.GroupBy(candidate => $"{candidate.Url}|{candidate.SourcePath}", StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First()).Take(100).ToArray(), Array.Empty<string>());
        var businessFlows = BusinessFlowAnalyzer.FromNextRoutes(root, relative, name, routeCandidates);
        return new ProjectDefinition(node, packageFile, businessFlows);
    }
}
