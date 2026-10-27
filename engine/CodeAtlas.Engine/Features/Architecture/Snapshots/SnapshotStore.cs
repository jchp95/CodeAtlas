using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

static class SnapshotStore
{
    public static string GetPath(string repositoryRoot)
    {
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(repositoryRoot)))).ToLowerInvariant()[..24];
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (string.IsNullOrWhiteSpace(appData)) appData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
        return Path.Combine(appData, "CodeAtlas", "snapshots", $"{key}.json");
    }

    public static ArchitectureSnapshot? Load(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            return JsonSerializer.Deserialize<ArchitectureSnapshot>(File.ReadAllText(path), JsonDefaults.Options);
        }
        catch { return null; }
    }

    public static void Save(string path, ArchitectureSnapshot snapshot)
    {
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(snapshot with { Delta = null }, JsonDefaults.Options));
        File.Move(temporary, path, true);
    }
}
