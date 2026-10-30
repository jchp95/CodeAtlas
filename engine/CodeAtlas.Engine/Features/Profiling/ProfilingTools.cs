/// <summary>Locates the official .NET diagnostic CLIs (dotnet-trace, dotnet-counters, dotnet-gcdump). Nothing is reimplemented: their output is parsed.</summary>
static class ProfilingTools
{
    public static readonly string[] Names = { "dotnet-trace", "dotnet-counters", "dotnet-gcdump" };

    public static string? Find(string name)
    {
        var candidates = new List<string?>
        {
            Environment.GetEnvironmentVariable("CODEATLAS_" + name.ToUpperInvariant().Replace('-', '_')),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codeatlas", "tools", "dotnet-tools", name)
        };
        candidates.AddRange((Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator).Select(p => Path.Combine(p, name)));
        return candidates.FirstOrDefault(c => !string.IsNullOrWhiteSpace(c) && File.Exists(c));
    }

    public static ToolStatus[] Doctor() => Names.Select(name =>
    {
        var path = Find(name);
        string? version = null;
        if (path is not null)
            try { version = BoundedProcess.Run(path, new[] { "--version" }, Environment.CurrentDirectory, TimeSpan.FromSeconds(20), CancellationToken.None, 3).Tail.FirstOrDefault(l => char.IsDigit(l.FirstOrDefault())); } catch { }
        return new ToolStatus(name, path, version, path is not null);
    }).ToArray();

    public static string InstallHint => "Install into a user directory (no global change): dotnet tool install <name> --tool-path ~/.codeatlas/tools/dotnet-tools   (or set CODEATLAS_DOTNET_TRACE / CODEATLAS_DOTNET_COUNTERS / CODEATLAS_DOTNET_GCDUMP).";

    /// <summary>Private output directory for traces and dumps: they can contain symbol names and, for dumps, process data.</summary>
    public static string PrepareOutputDirectory(string root, string kind)
    {
        var baseDirectory = Environment.GetEnvironmentVariable("CODEATLAS_DATA_DIR") is { Length: > 0 } custom ? custom
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData) is { Length: > 0 } a ? a : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config"), "CodeAtlas");
        var key = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Path.GetFullPath(root)))).ToLowerInvariant()[..16];
        var profiles = Path.Combine(baseDirectory, "profiles", key);
        Prune(profiles);
        var directory = Path.Combine(profiles, kind + "-" + DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmssfff"));
        Directory.CreateDirectory(directory);
        try { if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); } catch { }
        return directory;
    }

    /// <summary>Traces and heap dumps are kept for 14 days, then deleted: they are large and describe the profiled process.</summary>
    private static void Prune(string profiles)
    {
        try
        {
            if (!Directory.Exists(profiles)) return;
            foreach (var old in Directory.GetDirectories(profiles).Where(d => System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(d), @"^(cpu|counters|heap)-\d{8}-\d+$")
                         && Directory.GetLastWriteTimeUtc(d) < DateTime.UtcNow.AddDays(-14)))
                Directory.Delete(old, true);
        }
        catch { /* housekeeping must never fail a profile */ }
    }
}
