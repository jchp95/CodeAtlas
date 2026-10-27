sealed class StackPilotConfiguration
{
    public int Version { get; set; } = 1;
    public List<StackPilotProfile> Profiles { get; set; } = [];
}

sealed class StackPilotProfile
{
    public string Id { get; set; } = "local-dev";
    public string Name { get; set; } = "Local development";
    public string WorkspaceRoot { get; set; } = ".";
    public List<StackPilotService> Services { get; set; } = [];
}

sealed class StackPilotService
{
    public string Id { get; set; } = "service";
    public string Name { get; set; } = "Service";
    public bool Enabled { get; set; } = true;
    public string Kind { get; set; } = "process"; // process | compose
    public string Command { get; set; } = "";
    public string[] Arguments { get; set; } = [];
    public string WorkingDirectory { get; set; } = ".";
    public string? HealthUrl { get; set; }
    public string[] DependsOn { get; set; } = [];
    public Dictionary<string, string> Environment { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public string? ComposeFile { get; set; }
    public string? ComposeService { get; set; }
}

sealed record StackPilotResponse(bool Configured, string ConfigurationPath, string? Message, string? Error, StackPilotProfileStatus[] Profiles);
sealed record StackPilotProfileStatus(string Id, string Name, string WorkspaceRoot, StackPilotServiceStatus[] Services);
sealed record StackPilotServiceStatus(
    string Id, string Name, string Kind, bool Enabled, string Status, int? Pid,
    string WorkingDirectory, string Command, string? HealthUrl, string HealthStatus,
    string LogPath, string[] DependsOn, string? Error);
sealed record RuntimeState(int Pid, long StartedAtUnixMilliseconds, string LogPath, string Command, string WorkingDirectory);
