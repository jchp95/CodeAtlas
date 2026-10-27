using System.Diagnostics;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

static class RuntimePaths
{
    private static string Root
    {
        get
        {
            var applicationData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            if (OperatingSystem.IsMacOS() && string.IsNullOrWhiteSpace(applicationData))
                applicationData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Application Support");
            if (string.IsNullOrWhiteSpace(applicationData))
                applicationData = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
            return Path.Combine(applicationData, "CodeAtlas", "StackPilot");
        }
    }

    private static string Key(string profileId, string serviceId) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{profileId}/{serviceId}"))).ToLowerInvariant()[..24];

    public static string LogPath(string profileId, string serviceId) => Path.Combine(Root, "logs", $"{Key(profileId, serviceId)}.log");
    private static string StatePath(string profileId, string serviceId) => Path.Combine(Root, "runtime", $"{Key(profileId, serviceId)}.json");

    public static RuntimeState? LoadState(string profileId, string serviceId)
    {
        try
        {
            var path = StatePath(profileId, serviceId);
            return File.Exists(path) ? JsonSerializer.Deserialize<RuntimeState>(File.ReadAllText(path)) : null;
        }
        catch { return null; }
    }

    public static void SaveState(string profileId, string serviceId, RuntimeState state)
    {
        var path = StatePath(profileId, serviceId);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(state));
        File.Move(temporary, path, true);
    }

    public static void DeleteState(string profileId, string serviceId)
    {
        try { File.Delete(StatePath(profileId, serviceId)); } catch { }
    }
}
