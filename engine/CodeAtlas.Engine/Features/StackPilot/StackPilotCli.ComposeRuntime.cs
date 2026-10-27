using System.Diagnostics;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

static partial class StackPilotCli
{
private static ComposeResult RunCompose(StackPilotProfile profile, StackPilotService service, string[] commandArguments, int timeoutMilliseconds = 15000)
    {
        var working = ResolveWorkingDirectory(profile, service);
        if (!Directory.Exists(working)) return new ComposeResult(2, "", $"Compose working directory does not exist: {working}");
        if (string.IsNullOrWhiteSpace(service.ComposeFile)) return new ComposeResult(2, "", $"{service.Name} requires composeFile.");
        var composeFile = ResolvePath(working, service.ComposeFile);
        if (!File.Exists(composeFile)) return new ComposeResult(2, "", $"Compose file does not exist: {composeFile}");
        try
        {
            var start = new ProcessStartInfo("docker") { WorkingDirectory = working, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            start.ArgumentList.Add("compose");
            start.ArgumentList.Add("--file");
            start.ArgumentList.Add(composeFile);
            foreach (var argument in commandArguments) start.ArgumentList.Add(argument);
            using var process = Process.Start(start);
            if (process is null) return new ComposeResult(2, "", "Could not start Docker CLI.");
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(timeoutMilliseconds))
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                return new ComposeResult(124, "", $"Docker Compose command timed out after {timeoutMilliseconds / 1000.0:0.#} seconds.");
            }
            return new ComposeResult(process.ExitCode, stdout.GetAwaiter().GetResult(), stderr.GetAwaiter().GetResult());
        }
        catch (Exception ex) { return new ComposeResult(127, "", $"Docker CLI is unavailable: {ex.Message}"); }
    }

    private static string ComposeServiceName(StackPilotService service) =>
        string.IsNullOrWhiteSpace(service.ComposeService) ? service.Id : service.ComposeService;

    private static string ResolveWorkingDirectory(StackPilotProfile profile, StackPilotService service)
    {
        var root = string.IsNullOrWhiteSpace(profile.WorkspaceRoot) ? Environment.CurrentDirectory : ExpandPath(profile.WorkspaceRoot);
        return ResolvePath(root, string.IsNullOrWhiteSpace(service.WorkingDirectory) ? "." : service.WorkingDirectory);
    }

    private static string ResolvePath(string basePath, string path)
    {
        var expanded = ExpandPath(path);
        return Path.GetFullPath(Path.IsPathRooted(expanded) ? expanded : Path.Combine(basePath, expanded));
    }

    private static string ExpandPath(string path)
    {
        if (path == "~") return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (path.StartsWith("~/", StringComparison.Ordinal)) return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path[2..]);
        return Environment.ExpandEnvironmentVariables(path);
    }

    private static string BuildCommandPreview(StackPilotService service) => service.Kind.Equals("compose", StringComparison.OrdinalIgnoreCase)
        ? $"docker compose {service.ComposeFile} · {ComposeServiceName(service)}"
        : string.Join(" ", new[] { service.Command }.Concat(service.Arguments));

    private static IEnumerable<string> TailLines(string path, int count)
    {
        // Streaming queue keeps memory bounded for large logs.
        var queue = new Queue<string>(count);
        foreach (var line in File.ReadLines(path))
        {
            if (queue.Count == count) queue.Dequeue();
            queue.Enqueue(line);
        }
        return queue;
    }
}
