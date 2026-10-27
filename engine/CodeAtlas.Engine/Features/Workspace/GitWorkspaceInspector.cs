using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

static class GitWorkspaceInspector
{
    private static readonly HashSet<string> ExcludedDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".hg", ".svn", "node_modules", "bin", "obj", ".next", "dist", "build", "coverage",
        ".turbo", ".vercel", "vendor", "Pods", "DerivedData", "TestResults"
    };

    public static GitRepositoryState[] Scan(string workspaceRoot)
    {
        var root = Path.GetFullPath(workspaceRoot);
        var roots = DiscoverRoots(root);
        var states = new List<GitRepositoryState>();
        foreach (var (repositoryPath, relativePath) in roots.OrderBy(item => item.RelativePath, StringComparer.Ordinal))
        {
            var git = new GitInspector(repositoryPath);
            var branch = git.Run("branch", "--show-current");
            var head = git.Run("rev-parse", "--short", "HEAD");
            var status = git.Run("status", "--short", "--untracked-files=all");
            var changes = ParseChanges(status.Output, relativePath);
            var childRepositories = roots.Select(item => item.RelativePath)
                .Where(path => path != relativePath && IsNestedPath(path, relativePath))
                .ToArray();
            if (childRepositories.Length > 0)
            {
                changes = changes.Where(change => !childRepositories.Any(child =>
                    string.Equals(change.Path.TrimEnd('/'), child, StringComparison.Ordinal)
                    || child.StartsWith(change.Path.TrimEnd('/') + "/", StringComparison.Ordinal))).ToArray();
            }
            states.Add(new GitRepositoryState(
                relativePath,
                branch.ExitCode == 0 && branch.Output.Trim().Length > 0 ? branch.Output.Trim() : "(HEAD separado o rama desconocida)",
                head.ExitCode == 0 && head.Output.Trim().Length > 0 ? head.Output.Trim() : "(sin commit)",
                changes));
        }
        return states.ToArray();
    }

    private static List<(string RepositoryPath, string RelativePath)> DiscoverRoots(string root)
    {
        var found = new List<(string RepositoryPath, string RelativePath)>();
        var rootProbe = new GitInspector(root).Run("rev-parse", "--is-inside-work-tree");
        if (rootProbe.ExitCode == 0 && string.Equals(rootProbe.Output.Trim(), "true", StringComparison.OrdinalIgnoreCase))
            found.Add((root, "."));

        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            IEnumerable<string> children;
            try { children = Directory.EnumerateDirectories(directory).ToArray(); }
            catch { continue; }

            foreach (var child in children)
            {
                var name = Path.GetFileName(child);
                if (ExcludedDirectories.Contains(name)) continue;
                try
                {
                    if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) != 0) continue;
                }
                catch { continue; }
                var dotGit = Path.Combine(child, ".git");
                if ((Directory.Exists(dotGit) || File.Exists(dotGit)) && !PathTools.PathsEqual(child, root))
                    found.Add((child, PathTools.Relative(root, child)));
                // Continue below a Git root as well, so a nested service repository is discoverable.
                // The .git directory itself is excluded by the directory filter above.
                pending.Push(child);
            }
        }

        return found
            .GroupBy(item => Path.GetFullPath(item.RepositoryPath), OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)
            .Select(group => group.First())
            .ToList();
    }

    private static bool IsNestedPath(string candidate, string parent) => parent == "."
        ? candidate != "."
        : candidate.StartsWith(parent.TrimEnd('/') + "/", StringComparison.Ordinal);

    private static GitChange[] ParseChanges(string output, string relativeRepositoryPath) =>
        output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Where(line => line.Length >= 4)
            .Select(line =>
            {
                var status = line[..2].Trim();
                if (status.Length == 0) status = "M";
                var localPath = line[3..].Trim();
                var path = relativeRepositoryPath == "." ? localPath : $"{relativeRepositoryPath}/{localPath}";
                return new GitChange(status, path.Replace('\\', '/'));
            })
            .ToArray();
}
