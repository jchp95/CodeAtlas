using System.Diagnostics;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

static partial class StackPilotCli
{
private static StackPilotProfile CreateStarterProfile(string workspace)
    {
        var services = new List<StackPilotService>();
        var allFiles = EnumerateWorkspaceFiles(workspace).ToArray();
        foreach (var file in allFiles.Where(p => p.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                var document = XDocument.Load(file);
                var sdk = document.Root?.Attribute("Sdk")?.Value ?? string.Empty;
                var isExecutable = sdk.Contains("Web", StringComparison.OrdinalIgnoreCase) ||
                    document.Descendants().Any(e => e.Name.LocalName == "OutputType" && e.Value.Trim().Equals("Exe", StringComparison.OrdinalIgnoreCase));
                if (!isExecutable) continue;
                var relative = Path.GetRelativePath(workspace, file).Replace('\\', '/');
                var name = Path.GetFileNameWithoutExtension(file);
                services.Add(new StackPilotService
                {
                    Id = Slug(name), Name = name, Enabled = false, Kind = "process", Command = "dotnet",
                    Arguments = ["run", "--project", relative], WorkingDirectory = ".",
                    Environment = new Dictionary<string, string> { ["ASPNETCORE_ENVIRONMENT"] = "Development" }
                });
            }
            catch { /* Malformed project files are ignored; the architecture scanner reports its own diagnostics. */ }
        }

        var rootPackageManager = allFiles.Any(p => Path.GetFileName(p) == "pnpm-lock.yaml") ? "pnpm" :
            allFiles.Any(p => Path.GetFileName(p) == "yarn.lock") ? "yarn" : "npm";
        foreach (var file in allFiles.Where(p => Path.GetFileName(p) == "package.json"))
        {
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(file));
                var root = document.RootElement;
                if (!root.TryGetProperty("scripts", out var scripts) || scripts.ValueKind != JsonValueKind.Object) continue;
                var script = scripts.TryGetProperty("dev", out _) ? "dev" : scripts.TryGetProperty("start", out _) ? "start" : null;
                if (script is null) continue;
                var name = root.TryGetProperty("name", out var nameNode) ? nameNode.GetString() : null;
                name = string.IsNullOrWhiteSpace(name) ? Path.GetFileName(Path.GetDirectoryName(file)!) : name;
                var working = Path.GetRelativePath(workspace, Path.GetDirectoryName(file)!).Replace('\\', '/');
                var packageRoot = Path.GetDirectoryName(file)!;
                var localManager = File.Exists(Path.Combine(packageRoot, "pnpm-lock.yaml")) ? "pnpm" :
                    File.Exists(Path.Combine(packageRoot, "yarn.lock")) ? "yarn" : rootPackageManager;
                services.Add(new StackPilotService
                {
                    Id = Slug(name!), Name = name!, Enabled = false, Kind = "process", Command = localManager,
                    Arguments = ["run", script], WorkingDirectory = working == "." ? "." : working,
                    Environment = new Dictionary<string, string> { ["NODE_ENV"] = "development" }
                });
            }
            catch { }
        }

        // Ensure IDs remain unique even when multiple projects share a basename.
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var service in services)
        {
            var original = service.Id;
            var suffix = 2;
            while (!seen.Add(service.Id)) service.Id = $"{original}-{suffix++}";
        }

        var folderName = Path.GetFileName(workspace.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var profileId = Slug(string.IsNullOrWhiteSpace(folderName) ? "local-dev" : folderName);
        return new StackPilotProfile
        {
            Id = profileId, Name = $"Desarrollo local · {folderName}", WorkspaceRoot = workspace,
            Services = services
        };
    }

    private static IEnumerable<string> EnumerateWorkspaceFiles(string root)
    {
        var excluded = new HashSet<string>([".git", ".hg", ".svn", "node_modules", "bin", "obj", ".next", "dist", "build", "coverage", ".turbo", "vendor", "DerivedData"], StringComparer.OrdinalIgnoreCase);
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            IEnumerable<string> directories;
            IEnumerable<string> files;
            try
            {
                directories = Directory.EnumerateDirectories(directory).ToArray();
                files = Directory.EnumerateFiles(directory).ToArray();
            }
            catch { continue; }
            foreach (var file in files)
                if (Path.GetExtension(file).Equals(".csproj", StringComparison.OrdinalIgnoreCase) || Path.GetFileName(file) == "package.json" || (Path.GetFileName(file) is "pnpm-lock.yaml" or "yarn.lock"))
                    yield return file;
            foreach (var child in directories)
            {
                if (excluded.Contains(Path.GetFileName(child))) continue;
                try
                {
                    if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) != 0) continue;
                }
                catch { continue; }
                pending.Push(child);
            }
        }
    }
}
