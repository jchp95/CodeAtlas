using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

const string Usage = """
CodeAtlas.Engine
  scan <repository-path> [--no-persist]
  compare <baseline-snapshot.json> <current-snapshot.json>
  fingerprint <repository-path>
  http-catalog <repository-path>
  http-call <METHOD> <absolute-url> <headers-base64> <body-base64>
  http-file-run <repository-path> <relative-http-file> <request-index> <environment-name> <overrides-base64>
  analyze <repository-path> [--no-cache] [--rules ATL1001,ATL1003] [--max-parallelism N]
  analysis-rules
  fix <repository-path> <finding-id> [--apply [--base-hash HASH]]
  stackpilot <status|init|enable|disable|start|stop|start-profile|stop-profile|logs> [options]
""";

if (args.Length < 2 && !(args.Length == 1 && args[0].Equals("analysis-rules", StringComparison.OrdinalIgnoreCase)))
{
    Console.Error.WriteLine(Usage);
    return 2;
}

var jsonOptions = JsonDefaults.Options;
switch (args[0].ToLowerInvariant())
{
    case "analyze":
    {
        var root = Path.GetFullPath(args[1]);
        if (!Directory.Exists(root))
        {
            Console.Error.WriteLine($"Repository directory does not exist: {root}");
            return 2;
        }
        var rest = args.Skip(2).ToArray();
        var ruleIds = OptionValue(rest, "--rules")?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        int? parallelism = int.TryParse(OptionValue(rest, "--max-parallelism"), out var parsedParallelism) ? parsedParallelism : null;
        using var cancellation = new ProcessCancellation();
        var report = new CodeAnalysisEngine(root, new AnalysisOptions(!rest.Contains("--no-cache"), ruleIds, parallelism)).Run(cancellation.Token);
        Console.Write(JsonSerializer.Serialize(report, jsonOptions));
        return report.Cancelled ? 130 : 0;
    }
    case "analysis-rules":
        Console.Write(JsonSerializer.Serialize(CodeAnalysisEngine.Descriptors, jsonOptions));
        return 0;
    case "fix":
    {
        var root = Path.GetFullPath(args[1]);
        if (args.Length < 3 || !Directory.Exists(root))
        {
            Console.Error.WriteLine("Usage: CodeAtlas.Engine fix <repository-path> <finding-id> [--apply]");
            return 2;
        }
        using var cancellation = new ProcessCancellation();
        var report = new CodeAnalysisEngine(root, new AnalysisOptions()).Run(cancellation.Token);
        var finding = report.Findings.FirstOrDefault(f => f.Id.Equals(args[2], StringComparison.OrdinalIgnoreCase));
        if (finding is null)
        {
            Console.Error.WriteLine($"No finding with id {args[2]} exists in the current analysis.");
            return 3;
        }
        if (finding.Fix is null)
        {
            Console.Error.WriteLine($"{finding.RuleId} has no automatic fix for this finding; follow the recommendation manually.");
            return 4;
        }
        // The diff is always shown first; the file is modified only with an explicit --apply.
        Console.Write(finding.Fix.UnifiedDiff);
        var fixArguments = args.Skip(3).ToArray();
        if (!fixArguments.Contains("--apply")) return 0;
        // The caller passes the hash of the file it previewed so a change made after the preview is never overwritten.
        var previewedHash = OptionValue(fixArguments, "--base-hash");
        if (previewedHash is not null && !previewedHash.Equals(finding.Fix.BaseFileHash, StringComparison.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine($"{finding.Fix.File} changed after the preview was shown; the fix was not applied.");
            return 5;
        }
        var error = FixBuilder.TryApply(root, finding.Fix);
        if (error is not null)
        {
            Console.Error.WriteLine(error);
            return 5;
        }
        Console.Error.WriteLine($"Applied: {finding.Fix.Title}");
        return 0;
    }
    case "scan":
    {
        var root = Path.GetFullPath(args[1]);
        if (!Directory.Exists(root))
        {
            Console.Error.WriteLine($"Repository directory does not exist: {root}");
            return 2;
        }

        var persist = !args.Skip(2).Any(a => a.Equals("--no-persist", StringComparison.OrdinalIgnoreCase));
        var current = new RepositoryScanner(root).Scan();
        ArchitectureSnapshot? previous = null;
        string? storePath = null;

        if (persist)
        {
            storePath = SnapshotStore.GetPath(root);
            previous = SnapshotStore.Load(storePath);
            if (previous is not null && !PathTools.PathsEqual(previous.RepositoryPath, root)) previous = null;
        }

        var delta = previous is null ? null : ArchitectureComparer.Compare(previous, current);
        if (persist && storePath is not null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(storePath)!);
            SnapshotStore.Save(storePath, current);
        }

        Console.Write(JsonSerializer.Serialize(current with { Delta = delta }, jsonOptions));
        return 0;
    }
    case "compare":
    {
        if (args.Length < 3)
        {
            Console.Error.WriteLine("Usage: CodeAtlas.Engine compare <baseline-snapshot.json> <current-snapshot.json>");
            return 2;
        }
        var baseline = SnapshotStore.Load(Path.GetFullPath(args[1]));
        var current = SnapshotStore.Load(Path.GetFullPath(args[2]));
        if (baseline is null || current is null)
        {
            Console.Error.WriteLine("Could not read one of the snapshot files.");
            return 2;
        }
        Console.Write(JsonSerializer.Serialize(ArchitectureComparer.Compare(baseline, current), jsonOptions));
        return 0;
    }
    case "http-catalog":
    {
        var root = Path.GetFullPath(args[1]);
        if (!Directory.Exists(root))
        {
            Console.Error.WriteLine($"Workspace directory does not exist: {root}");
            return 2;
        }
        var snapshot = new RepositoryScanner(root).Scan();
        var catalog = HttpTestingCatalogBuilder.Build(root, snapshot.Projects);
        Console.Write(JsonSerializer.Serialize(catalog, jsonOptions));
        return 0;
    }
    case "http-call":
    {
        if (args.Length < 5)
        {
            Console.Error.WriteLine("Usage: http-call <METHOD> <absolute-url> <headers-base64> <body-base64>");
            return 2;
        }
        try
        {
            var headers = Encoding.UTF8.GetString(Convert.FromBase64String(args[3]));
            var body = Encoding.UTF8.GetString(Convert.FromBase64String(args[4]));
            var result = await HttpRequestExecutor.ExecuteRawAsync(args[1], args[2], headers, body);
            Console.Write(JsonSerializer.Serialize(result, jsonOptions));
            return 0;
        }
        catch (Exception ex)
        {
            Console.Write(JsonSerializer.Serialize(new HttpExecutionResult(false, 0, "", 0, args[2], new Dictionary<string, string>(), "", false, ex.Message), jsonOptions));
            return 0;
        }
    }
    case "http-file-run":
    {
        if (args.Length < 6)
        {
            Console.Error.WriteLine("Usage: http-file-run <repository-path> <relative-http-file> <request-index> <environment-name> <overrides-base64>");
            return 2;
        }
        if (!int.TryParse(args[3], out var requestIndex) || requestIndex < 1)
        {
            Console.Error.WriteLine("Request index must be a positive integer.");
            return 2;
        }
        var result = await HttpRequestExecutor.ExecuteFileAsync(Path.GetFullPath(args[1]), args[2], requestIndex, args[4], args[5]);
        Console.Write(JsonSerializer.Serialize(result, jsonOptions));
        return 0;
    }
    case "stackpilot":
        return await StackPilotCli.RunAsync(args.Skip(1).ToArray());
    case "fingerprint":
    {
        var root = Path.GetFullPath(args[1]);
        if (!Directory.Exists(root))
        {
            Console.Error.WriteLine($"Repository directory does not exist: {root}");
            return 2;
        }
        Console.Write(RepositoryScanner.GetWorkspaceFingerprint(root));
        return 0;
    }
    default:
        Console.Error.WriteLine(Usage);
        return 2;
}

static string? OptionValue(string[] arguments, string name)
{
    var index = Array.FindIndex(arguments, a => a.Equals(name, StringComparison.OrdinalIgnoreCase));
    return index >= 0 && index + 1 < arguments.Length ? arguments[index + 1] : null;
}
