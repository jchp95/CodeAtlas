using System.Diagnostics;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

/// <summary>
/// Local process and Docker Compose control for StackPilot. Commands are launched with
/// ProcessStartInfo.ArgumentList; user-provided commands are never interpolated into a shell
/// command string. The only shell invocation is a constant wrapper whose positional arguments
/// carry the configured executable and arguments, allowing safe stdout/stderr redirection.
/// </summary>
static partial class StackPilotCli
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMilliseconds(1400) };
    private const string Usage = """
StackPilot local service manager
  stackpilot status [--config <file>]
  stackpilot init [--workspace <directory>] [--config <file>]
  stackpilot enable <profile-id> <service-id> [--config <file>]
  stackpilot disable <profile-id> <service-id> [--config <file>]
  stackpilot start <profile-id> <service-id> [--config <file>]
  stackpilot stop <profile-id> <service-id> [--config <file>]
  stackpilot start-profile <profile-id> [--config <file>]
  stackpilot stop-profile <profile-id> [--config <file>]
  stackpilot logs <profile-id> <service-id> [--lines <count>] [--config <file>]

Services are disabled by default when generated from a workspace. Review the command and paths
in the configuration file before enabling a service.
""";

    public static async Task<int> RunAsync(string[] rawArgs)
    {
        if (rawArgs.Length == 0 || rawArgs[0] is "help" or "--help" or "-h")
        {
            Console.WriteLine(Usage);
            return 0;
        }

        var args = rawArgs.ToList();
        var configPath = GetDefaultConfigPath();
        if (TryReadOption(args, "--config", out var configuredPath)) configPath = Path.GetFullPath(ExpandPath(configuredPath!));
        var command = args[0].ToLowerInvariant();
        args.RemoveAt(0);

        try
        {
            if (command == "init")
            {
                var workspace = Path.GetFullPath(TryReadOption(args, "--workspace", out var workspaceValue)
                    ? ExpandPath(workspaceValue!)
                    : Environment.CurrentDirectory);
                if (!Directory.Exists(workspace))
                    return WriteResponse(ErrorResponse(configPath, $"Workspace does not exist: {workspace}"));
                var config = File.Exists(configPath) ? LoadConfiguration(configPath) : new StackPilotConfiguration();
                var existing = config.Profiles.FirstOrDefault(p => PathEquals(p.WorkspaceRoot, workspace));
                if (existing is not null)
                    return WriteResponse(await CreateResponseAsync(configPath, config, $"Workspace already exists as profile '{existing.Id}'. No files were changed."));

                var profile = CreateStarterProfile(workspace);
                var baseProfileId = profile.Id;
                var suffix = 2;
                while (config.Profiles.Any(p => p.Id.Equals(profile.Id, StringComparison.OrdinalIgnoreCase)))
                    profile.Id = $"{baseProfileId}-{suffix++}";
                config.Profiles.Add(profile);
                SaveConfiguration(configPath, config);
                return WriteResponse(await CreateResponseAsync(configPath, config,
                    $"Added profile '{profile.Id}' with {profile.Services.Count} disabled service template(s). Review command, paths and health checks before enabling services."));
            }

            if (command == "logs")
            {
                if (args.Count < 2) return WriteTextError("Usage: stackpilot logs <profile-id> <service-id> [--lines <count>]");
                var profileId = args[0];
                var serviceId = args[1];
                var lines = 100;
                if (TryReadOption(args, "--lines", out var lineValue) && (!int.TryParse(lineValue, out lines) || lines is < 1 or > 5000))
                    return WriteTextError("--lines must be between 1 and 5000.");
                var config = LoadConfiguration(configPath);
                var (profile, service) = FindService(config, profileId, serviceId);
                if (profile is null || service is null) return WriteTextError($"Unknown profile/service: {profileId}/{serviceId}");
                if (service.Kind.Equals("compose", StringComparison.OrdinalIgnoreCase))
                {
                    var result = RunCompose(profile, service, ["logs", "--tail", lines.ToString(), ComposeServiceName(service)]);
                    Console.Write(result.Output);
                    if (!string.IsNullOrWhiteSpace(result.Error)) Console.Error.Write(result.Error);
                    return result.ExitCode;
                }
                var logPath = RuntimePaths.LogPath(profile.Id, service.Id);
                if (!File.Exists(logPath)) return WriteTextError($"No log file exists yet for {service.Name}. Expected: {logPath}");
                foreach (var line in TailLines(logPath, lines)) Console.WriteLine(line);
                return 0;
            }

            if (command == "status" || command == "profiles" || command == "doctor")
            {
                if (!File.Exists(configPath))
                    return WriteResponse(new StackPilotResponse(false, configPath,
                        "StackPilot is not configured yet. Initialize a starter configuration, then review each service before enabling it.", null, []));
                var config = LoadConfiguration(configPath);
                return WriteResponse(await CreateResponseAsync(configPath, config,
                    command == "doctor" ? "Environment check completed. Review unavailable services and health-check results." : null));
            }

            if (command is "enable" or "disable")
            {
                if (args.Count < 2) return WriteResponse(ErrorResponse(configPath, $"Usage: stackpilot {command} <profile-id> <service-id>"));
                var config = LoadConfiguration(configPath);
                var (profile, service) = FindService(config, args[0], args[1]);
                if (profile is null || service is null)
                    return WriteResponse((await CreateResponseAsync(configPath, config, null)) with { Error = $"Unknown profile/service: {args[0]}/{args[1]}" });
                if (command == "disable" && await IsRunningAsync(profile, service))
                    return WriteResponse((await CreateResponseAsync(configPath, config, null)) with { Error = "Stop the service before disabling it." });
                service.Enabled = command == "enable";
                SaveConfiguration(configPath, config);
                return WriteResponse(await CreateResponseAsync(configPath, config,
                    $"{service.Name} {(service.Enabled ? "enabled" : "disabled")} in configuration."));
            }

            if (command is "start" or "stop")
            {
                if (args.Count < 2) return WriteResponse(ErrorResponse(configPath, $"Usage: stackpilot {command} <profile-id> <service-id>"));
                var config = LoadConfiguration(configPath);
                var (profile, service) = FindService(config, args[0], args[1]);
                if (profile is null || service is null)
                    return WriteResponse((await CreateResponseAsync(configPath, config, null)) with { Error = $"Unknown profile/service: {args[0]}/{args[1]}" });
                var result = command == "start"
                    ? await StartServiceAsync(profile, service)
                    : await StopServiceAsync(profile, service);
                if (!result.Success)
                    return WriteResponse((await CreateResponseAsync(configPath, config, null)) with { Error = result.Message });
                return WriteResponse(await CreateResponseAsync(configPath, config, result.Message));
            }

            if (command is "start-profile" or "stop-profile")
            {
                if (args.Count < 1) return WriteResponse(ErrorResponse(configPath, $"Usage: stackpilot {command} <profile-id>"));
                var config = LoadConfiguration(configPath);
                var profile = config.Profiles.FirstOrDefault(p => p.Id.Equals(args[0], StringComparison.OrdinalIgnoreCase));
                if (profile is null)
                    return WriteResponse((await CreateResponseAsync(configPath, config, null)) with { Error = $"Unknown profile: {args[0]}" });
                var ordered = TopologicalOrder(profile);
                if (ordered.Error is not null)
                    return WriteResponse((await CreateResponseAsync(configPath, config, null)) with { Error = ordered.Error });
                var services = command == "start-profile" ? ordered.Services : ordered.Services.Reverse().ToArray();
                var messages = new List<string>();
                foreach (var service in services)
                {
                    if (command == "start-profile" && !service.Enabled) continue;
                    if (command == "stop-profile" && !await IsRunningAsync(profile, service))
                    {
                        messages.Add($"{service.Name}: already stopped");
                        continue;
                    }
                    var result = command == "start-profile"
                        ? await StartServiceAsync(profile, service)
                        : await StopServiceAsync(profile, service);
                    messages.Add($"{service.Name}: {result.Message}");
                    if (!result.Success)
                        return WriteResponse((await CreateResponseAsync(configPath, config, null)) with { Error = string.Join(Environment.NewLine, messages) });
                }
                return WriteResponse(await CreateResponseAsync(configPath, config, string.Join(" · ", messages)));
            }

            Console.WriteLine(Usage);
            return 0;
        }
        catch (Exception ex)
        {
            return WriteResponse(ErrorResponse(configPath, ex.Message));
        }
    }
}
