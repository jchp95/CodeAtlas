using System.Diagnostics;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

static partial class StackPilotCli
{
private static string GetDefaultConfigPath()
    {
        var applicationData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (OperatingSystem.IsMacOS())
        {
            if (string.IsNullOrWhiteSpace(applicationData))
                applicationData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Application Support");
        }
        else if (string.IsNullOrWhiteSpace(applicationData))
        {
            applicationData = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
        }
        return Path.Combine(applicationData, "CodeAtlas", "StackPilot", "profiles.json");
    }

    private static bool TryReadOption(List<string> args, string option, out string? value)
    {
        value = null;
        var index = args.FindIndex(a => a.Equals(option, StringComparison.OrdinalIgnoreCase));
        if (index < 0) return false;
        if (index + 1 >= args.Count) throw new ArgumentException($"Missing value for {option}.");
        value = args[index + 1];
        args.RemoveAt(index + 1);
        args.RemoveAt(index);
        return true;
    }

    private static bool PathEquals(string left, string right)
    {
        try
        {
            var a = Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var b = Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return string.Equals(a, b, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        }
        catch { return false; }
    }

    private static string Slug(string text)
    {
        var value = Regex.Replace(text.Trim().ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
        return string.IsNullOrEmpty(value) ? "service" : value;
    }

    private static StackPilotConfiguration LoadConfiguration(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("StackPilot configuration file was not found.", path);
        var config = JsonSerializer.Deserialize<StackPilotConfiguration>(File.ReadAllText(path), JsonOptions)
            ?? throw new InvalidDataException("StackPilot configuration file is empty or invalid JSON.");
        if (config.Version != 1) throw new InvalidDataException($"Unsupported StackPilot configuration version: {config.Version}.");
        if (config.Profiles is null) throw new InvalidDataException("Configuration field 'profiles' must be an array.");
        var duplicateProfile = config.Profiles.GroupBy(p => p.Id, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1);
        if (duplicateProfile is not null) throw new InvalidDataException($"Duplicate profile ID: {duplicateProfile.Key}.");
        foreach (var profile in config.Profiles)
        {
            if (string.IsNullOrWhiteSpace(profile.Id) || string.IsNullOrWhiteSpace(profile.Name))
                throw new InvalidDataException("Every profile must have non-empty id and name values.");
            if (profile.Services is null) throw new InvalidDataException($"Profile '{profile.Id}' must define services as an array.");
            foreach (var service in profile.Services)
            {
                if (string.IsNullOrWhiteSpace(service.Id) || string.IsNullOrWhiteSpace(service.Name))
                    throw new InvalidDataException($"Every service in profile '{profile.Id}' must have non-empty id and name values.");
                if (string.IsNullOrWhiteSpace(service.Kind) ||
                    (!service.Kind.Equals("process", StringComparison.OrdinalIgnoreCase) && !service.Kind.Equals("compose", StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidDataException($"Service '{profile.Id}/{service.Id}' has unsupported kind '{service.Kind}'. Use 'process' or 'compose'.");
                service.Arguments ??= [];
                service.DependsOn ??= [];
                service.Environment ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }
        }
        var duplicate = config.Profiles.SelectMany(p => p.Services.Select(s => (Profile: p.Id, Service: s.Id)))
            .GroupBy(x => $"{x.Profile}/{x.Service}", StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null) throw new InvalidDataException($"Duplicate service ID: {duplicate.Key}.");
        return config;
    }

    private static void SaveConfiguration(string path, StackPilotConfiguration config)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(config, JsonOptions));
        File.Move(temporary, path, true);
    }

    private static StackPilotResponse ErrorResponse(string configPath, string error, StackPilotConfiguration? config = null) =>
        new(config is not null, configPath, null, error, config?.Profiles.Select(p => new StackPilotProfileStatus(p.Id, p.Name, p.WorkspaceRoot, [])).ToArray() ?? []);

    private static int WriteResponse(StackPilotResponse response)
    {
        Console.WriteLine(JsonSerializer.Serialize(response, JsonOptions));
        return 0; // Operational errors are represented in JSON to keep the desktop client protocol stable.
    }

    private static int WriteTextError(string error) { Console.Error.WriteLine(error); return 2; }
}
