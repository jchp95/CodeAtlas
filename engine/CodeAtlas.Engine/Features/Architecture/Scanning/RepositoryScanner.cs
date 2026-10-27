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
    private readonly string root;

    public RepositoryScanner(string root) => this.root = root;

    private static readonly HashSet<string> ExcludedDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".hg", ".svn", "node_modules", "bin", "obj", ".next", "dist", "build", "coverage",
        ".turbo", ".vercel", "vendor", "Pods", "DerivedData", "TestResults"
    };

    public ArchitectureSnapshot Scan()
    {
        var warnings = new List<string>();
        var definitions = new List<ProjectDefinition>();

        try
        {
            // Build a repository-wide source type index once so cross-project contract
            // names remain explainable even when a local project cannot load its reference assembly.
            var repositoryTypeIndex = BuildRepositoryTypeIndex(warnings);
            foreach (var file in EnumerateFiles(root, "*.csproj"))
                definitions.Add(BuildDotnetProject(file, warnings, repositoryTypeIndex));

            foreach (var file in EnumerateFiles(root, "package.json"))
                definitions.Add(BuildNodeProject(file, warnings));
        }
        catch (Exception ex)
        {
            warnings.Add($"Some files could not be analyzed: {ex.Message}");
        }

        var projectList = definitions.Select(d => d.Node).ToList();
        var infrastructure = DetectInfrastructure(projectList.ToArray(), definitions);
        projectList.AddRange(infrastructure.Nodes);
        var projects = projectList.ToArray();
        var businessFlows = definitions.SelectMany(d => d.BusinessFlows ?? Array.Empty<BusinessFlow>()).ToArray();
        var relations = BuildRelations(projects).ToList();
        relations.AddRange(infrastructure.Relations);
        relations.AddRange(DetectTypeScriptModuleRelations(projects, warnings));
        relations = relations.GroupBy(r => r.Id, StringComparer.Ordinal).Select(g => g.First()).ToList();
        var distributedFlows = BuildDistributedFlows(projects, businessFlows, relations.ToArray());
        var gitRepositories = GitWorkspaceInspector.Scan(root);
        var changes = gitRepositories.SelectMany(repository => repository.Changes ?? Array.Empty<GitChange>()).ToArray();
        var branch = gitRepositories.Length == 0
            ? "(sin Git)"
            : gitRepositories.Length == 1
                ? gitRepositories[0].Branch
                : $"workspace ({gitRepositories.Length} repos)";
        var commit = gitRepositories.Length == 0
            ? "(sin commit)"
            : gitRepositories.Length == 1
                ? gitRepositories[0].HeadCommit
                : "workspace@" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                    string.Join("\n", gitRepositories.Select(repository => $"{repository.Path}|{repository.Branch}|{repository.HeadCommit}"))))).ToLowerInvariant()[..8];
        var repoName = Path.GetFileName(root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var dotnetCount = projects.Count(p => p.Kind == "Dotnet");
        var nodeCount = projects.Count(p => p.Kind is "Next.js" or "Node.js");
        var endpointCount = projects.Sum(p => p.Endpoints.Length);
        var publishedCount = projects.Sum(p => p.ContractsPublished.Length);
        var consumedCount = projects.Sum(p => p.ContractsConsumed.Length);

        if (projects.Length == 0)
            warnings.Add("No .csproj or package.json files were found. Select the root of a solution or monorepo.");
        if (branch == "(sin Git)")
            warnings.Add("Git branch could not be detected. The selected folder may not be a Git repository.");
        if (relations.Count == 0 && projects.Length > 1)
            warnings.Add("No cross-project relationships were confirmed. This does not mean that the services are disconnected; some links may require runtime or configuration evidence.");
        if (publishedCount + consumedCount > 0 && !relations.Any(r => r.Type == "message-flow"))
            warnings.Add("Message candidates were found, but no producer-consumer pair was linked by a matching contract name.");

        return new ArchitectureSnapshot(
            root,
            string.IsNullOrWhiteSpace(repoName) ? root : repoName,
            branch,
            commit,
            DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss zzz"),
            projects.OrderBy(p => p.Kind).ThenBy(p => p.Name).ToArray(),
            relations.OrderBy(r => r.Type).ThenBy(r => r.SourceId).ThenBy(r => r.TargetId).ToArray(),
            changes,
            warnings.Distinct().ToArray(),
            new ScanSummary(dotnetCount, nodeCount, endpointCount, changes.Length, relations.Count, publishedCount, consumedCount),
            null,
            businessFlows,
            distributedFlows,
            gitRepositories);
    }
}
