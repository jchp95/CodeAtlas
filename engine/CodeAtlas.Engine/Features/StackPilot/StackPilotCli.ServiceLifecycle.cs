using System.Diagnostics;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

static partial class StackPilotCli
{
private static async Task<StackPilotResponse> CreateResponseAsync(string configPath, StackPilotConfiguration config, string? message)
    {
        var profileTasks = config.Profiles.Select(async profile =>
        {
            var services = await Task.WhenAll(profile.Services.Select(service => GetServiceStatusAsync(profile, service)));
            return new StackPilotProfileStatus(profile.Id, profile.Name, profile.WorkspaceRoot,
                services.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToArray());
        });
        var statuses = await Task.WhenAll(profileTasks);
        return new StackPilotResponse(true, configPath, message, null, statuses);
    }

    private static async Task<StackPilotServiceStatus> GetServiceStatusAsync(StackPilotProfile profile, StackPilotService service)
    {
        var working = ResolveWorkingDirectory(profile, service);
        var logPath = RuntimePaths.LogPath(profile.Id, service.Id);
        var status = service.Enabled ? "stopped" : "disabled";
        int? pid = null;
        string? error = null;

        if (service.Kind.Equals("compose", StringComparison.OrdinalIgnoreCase))
        {
            var result = RunCompose(profile, service, ["ps", "--status", "running", "--services"], 3500);
            if (result.ExitCode == 0)
            {
                var isComposeServiceRunning = result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Contains(ComposeServiceName(service), StringComparer.OrdinalIgnoreCase);
                status = isComposeServiceRunning ? "running" : (service.Enabled ? "stopped" : "disabled");
            }
            else if (service.Enabled)
            {
                status = "unavailable";
                error = string.IsNullOrWhiteSpace(result.Error) ? "Docker Compose status check failed." : result.Error.Trim();
            }
        }
        else
        {
            var state = RuntimePaths.LoadState(profile.Id, service.Id);
            if (state is not null && TryGetManagedProcess(state, out var process))
            {
                using (process) { pid = state.Pid; status = "running"; }
            }
        }

        var health = "not-configured";
        if (status == "running" && Uri.TryCreate(service.HealthUrl, UriKind.Absolute, out var uri) &&
            (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            try
            {
                using var response = await Http.GetAsync(uri);
                health = response.IsSuccessStatusCode ? "healthy" : $"http-{(int)response.StatusCode}";
            }
            catch (Exception ex)
            {
                health = "unhealthy";
                error ??= ex is TaskCanceledException ? "Health check timed out." : ex.Message;
            }
        }

        return new StackPilotServiceStatus(service.Id, service.Name, service.Kind, service.Enabled, status, pid,
            working, BuildCommandPreview(service), service.HealthUrl, health, logPath, service.DependsOn, error);
    }

    private static async Task<ActionResult> StartServiceAsync(StackPilotProfile profile, StackPilotService service)
    {
        if (!service.Enabled) return new ActionResult(false, $"{service.Name} is disabled. Enable it after reviewing its configuration.");
        if (service.DependsOn.Any(id => profile.Services.All(s => !s.Id.Equals(id, StringComparison.OrdinalIgnoreCase))))
            return new ActionResult(false, $"{service.Name} references an unknown dependency in dependsOn.");
        foreach (var dependencyId in service.DependsOn)
        {
            var dependency = profile.Services.First(s => s.Id.Equals(dependencyId, StringComparison.OrdinalIgnoreCase));
            if (!dependency.Enabled) return new ActionResult(false, $"Dependency {dependency.Name} is disabled.");
            if (!await IsRunningAsync(profile, dependency)) return new ActionResult(false, $"Start dependency {dependency.Name} first.");
        }

        if (await IsRunningAsync(profile, service)) return new ActionResult(true, $"{service.Name} is already running.");
        var working = ResolveWorkingDirectory(profile, service);
        if (!Directory.Exists(working)) return new ActionResult(false, $"Working directory does not exist: {working}");

        if (service.Kind.Equals("compose", StringComparison.OrdinalIgnoreCase))
        {
            var result = RunCompose(profile, service, ["up", "-d", ComposeServiceName(service)]);
            return result.ExitCode == 0
                ? new ActionResult(true, $"Started Docker Compose service {ComposeServiceName(service)}.")
                : new ActionResult(false, string.IsNullOrWhiteSpace(result.Error) ? result.Output : result.Error.Trim());
        }

        if (string.IsNullOrWhiteSpace(service.Command)) return new ActionResult(false, $"{service.Name} has no command configured.");
        var logPath = RuntimePaths.LogPath(profile.Id, service.Id);
        Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
        try
        {
            var startInfo = new ProcessStartInfo("/bin/sh")
            {
                WorkingDirectory = working,
                UseShellExecute = false,
                RedirectStandardInput = true
            };
            startInfo.ArgumentList.Add("-c");
            startInfo.ArgumentList.Add("exec \"$@\" >> \"$CODEATLAS_SERVICE_LOG\" 2>&1");
            startInfo.ArgumentList.Add("codeatlas-launch");
            startInfo.ArgumentList.Add(service.Command);
            foreach (var argument in service.Arguments) startInfo.ArgumentList.Add(argument);
            startInfo.Environment["CODEATLAS_SERVICE_LOG"] = logPath;
            foreach (var pair in service.Environment) startInfo.Environment[pair.Key] = pair.Value;

            using var process = Process.Start(startInfo);
            if (process is null) return new ActionResult(false, "The operating system did not create the process.");
            process.StandardInput.Close();
            await Task.Delay(250);
            if (process.HasExited)
            {
                var tail = File.Exists(logPath) ? string.Join(Environment.NewLine, TailLines(logPath, 20)) : "No log output.";
                return new ActionResult(false, $"{service.Name} exited during startup (code {process.ExitCode}). {tail}");
            }
            var startedAt = 0L;
            try { startedAt = new DateTimeOffset(process.StartTime.ToUniversalTime()).ToUnixTimeMilliseconds(); } catch { startedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(); }
            RuntimePaths.SaveState(profile.Id, service.Id, new RuntimeState(process.Id, startedAt, logPath, service.Command, working));
            return new ActionResult(true, $"Started {service.Name} (PID {process.Id}). Logs: {logPath}");
        }
        catch (Exception ex)
        {
            return new ActionResult(false, $"Could not start {service.Name}: {ex.Message}");
        }
    }

    private static async Task<ActionResult> StopServiceAsync(StackPilotProfile profile, StackPilotService service)
    {
        if (service.Kind.Equals("compose", StringComparison.OrdinalIgnoreCase))
        {
            var result = RunCompose(profile, service, ["stop", ComposeServiceName(service)]);
            return result.ExitCode == 0
                ? new ActionResult(true, $"Stopped Docker Compose service {ComposeServiceName(service)}.")
                : new ActionResult(false, string.IsNullOrWhiteSpace(result.Error) ? result.Output : result.Error.Trim());
        }

        var state = RuntimePaths.LoadState(profile.Id, service.Id);
        if (state is null || !TryGetManagedProcess(state, out var process))
        {
            RuntimePaths.DeleteState(profile.Id, service.Id);
            return new ActionResult(true, $"{service.Name} is not running under StackPilot.");
        }
        using (process)
        {
            try
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(7));
                RuntimePaths.DeleteState(profile.Id, service.Id);
                return new ActionResult(true, $"Stopped {service.Name} (PID {state.Pid}) and its child processes.");
            }
            catch (TimeoutException)
            {
                return new ActionResult(false, $"Timed out waiting for {service.Name} to stop. Inspect its logs and process tree.");
            }
            catch (Exception ex)
            {
                return new ActionResult(false, $"Could not stop {service.Name}: {ex.Message}");
            }
        }
    }

    private static async Task<bool> IsRunningAsync(StackPilotProfile profile, StackPilotService service)
    {
        if (service.Kind.Equals("compose", StringComparison.OrdinalIgnoreCase))
        {
            var result = RunCompose(profile, service, ["ps", "--status", "running", "--services"], 3500);
            return result.ExitCode == 0 && result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Contains(ComposeServiceName(service), StringComparer.OrdinalIgnoreCase);
        }
        var state = RuntimePaths.LoadState(profile.Id, service.Id);
        if (state is null || !TryGetManagedProcess(state, out var process)) return false;
        process.Dispose();
        return true;
    }

    private static bool TryGetManagedProcess(RuntimeState state, out Process process)
    {
        process = null!;
        try
        {
            var candidate = Process.GetProcessById(state.Pid);
            if (candidate.HasExited) { candidate.Dispose(); return false; }
            long actual;
            try { actual = new DateTimeOffset(candidate.StartTime.ToUniversalTime()).ToUnixTimeMilliseconds(); }
            catch { candidate.Dispose(); return false; }
            // Protect against PID reuse. macOS process start time is typically rounded to a second.
            if (Math.Abs(actual - state.StartedAtUnixMilliseconds) > 2200) { candidate.Dispose(); return false; }
            process = candidate;
            return true;
        }
        catch { return false; }
    }

    private static (StackPilotService[] Services, string? Error) TopologicalOrder(StackPilotProfile profile)
    {
        var byId = profile.Services.ToDictionary(s => s.Id, StringComparer.OrdinalIgnoreCase);
        var visiting = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var output = new List<StackPilotService>();
        string? error = null;
        void Visit(StackPilotService service)
        {
            if (error is not null || visited.Contains(service.Id)) return;
            if (!visiting.Add(service.Id)) { error = $"Dependency cycle detected at {service.Name}."; return; }
            foreach (var dependencyId in service.DependsOn)
            {
                if (!byId.TryGetValue(dependencyId, out var dependency)) { error = $"{service.Name} references unknown dependency '{dependencyId}'."; return; }
                Visit(dependency);
            }
            visiting.Remove(service.Id);
            visited.Add(service.Id);
            output.Add(service);
        }
        foreach (var service in profile.Services) Visit(service);
        return (output.ToArray(), error);
    }

    private static (StackPilotProfile? Profile, StackPilotService? Service) FindService(StackPilotConfiguration config, string profileId, string serviceId)
    {
        var profile = config.Profiles.FirstOrDefault(p => p.Id.Equals(profileId, StringComparison.OrdinalIgnoreCase));
        var service = profile?.Services.FirstOrDefault(s => s.Id.Equals(serviceId, StringComparison.OrdinalIgnoreCase));
        return (profile, service);
    }
}
