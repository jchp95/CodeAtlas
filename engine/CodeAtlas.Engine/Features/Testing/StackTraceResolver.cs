using System.Text.RegularExpressions;

/// <summary>
/// Maps frames of a .NET stack trace to call-graph nodes. A frame resolves only when exactly one source member matches its
/// type, name and parameter count (or the file and line fall inside exactly one member); otherwise it is reported as
/// ambiguous or external, never guessed.
/// </summary>
static partial class StackTraceResolver
{
    [GeneratedRegex(@"^\s*at\s+(?<sym>.+?)\((?<args>[^)]*)\)(?:\s+in\s+(?<file>.+?):line\s+(?<line>\d+))?\s*$")]
    private static partial Regex FrameRegex();

    public static StackFrame[] Resolve(string? stackTrace, GraphIndex? index, string repositoryRoot)
    {
        if (string.IsNullOrWhiteSpace(stackTrace)) return Array.Empty<StackFrame>();
        var frames = new List<StackFrame>();
        foreach (var raw in stackTrace.Split('\n'))
        {
            var match = FrameRegex().Match(raw.TrimEnd('\r'));
            if (!match.Success) continue;
            var symbol = match.Groups["sym"].Value.Trim();
            var file = match.Groups["file"].Success ? RelativeTo(repositoryRoot, match.Groups["file"].Value) : null;
            int? line = match.Groups["line"].Success ? int.Parse(match.Groups["line"].Value) : null;
            var arguments = match.Groups["args"].Value.Trim();
            var parameterCount = arguments.Length == 0 ? 0 : arguments.Split(',').Length;
            var (node, resolution) = index is null ? (null, "notResolved") : Find(index, symbol, parameterCount, file, line);
            frames.Add(new StackFrame(symbol + "(" + arguments + ")", file, line, node?.Id, resolution));
        }
        return frames.ToArray();
    }

    /// <summary>Resolves one symbol such as <c>Ns.Type.Method(args)</c> (CLR or C# spelling of the arguments) to a call-graph node.</summary>
    public static (GraphNode? Node, string Resolution) ResolveSymbol(GraphIndex index, string symbolWithArguments)
    {
        var open = symbolWithArguments.IndexOf('(');
        var symbol = open < 0 ? symbolWithArguments : symbolWithArguments[..open];
        var arguments = open < 0 ? "" : symbolWithArguments[(open + 1)..].TrimEnd(')');
        // Generic argument lists contain commas that are not parameter separators.
        var depth = 0; var count = arguments.Trim().Length == 0 ? 0 : 1;
        foreach (var c in arguments) { if (c is '<' or '[' or '(') depth++; else if (c is '>' or ']' or ')') depth--; else if (c == ',' && depth == 0) count++; }
        return Find(index, symbol, count, null, null);
    }

    private static (GraphNode? Node, string Resolution) Find(GraphIndex index, string symbol, int parameterCount, string? file, int? line)
    {
        var (type, method) = Normalize(symbol);
        if (type is null) return (null, "unparsed");

        var candidates = index.Graph.Nodes.Where(n => !n.IsExternal && n.Kind != "initializer" && n.ContainingType is not null
            && (n.ContainingType == type || n.ContainingType.EndsWith("." + type, StringComparison.Ordinal))
            && (n.Name == method || (method == ".ctor" && n.Kind == "constructor") || (n.Kind == "accessor" && n.Name.EndsWith("_" + method, StringComparison.Ordinal)))).ToArray();
        if (candidates.Length > 1)
        {
            var byArity = candidates.Where(n => ParameterCount(n.Signature) == parameterCount).ToArray();
            if (byArity.Length >= 1) candidates = byArity;
        }
        if (candidates.Length > 1 && file is not null && line is not null)
        {
            var byLocation = candidates.Where(n => n.Location is not null && n.Location.File.Equals(file, StringComparison.OrdinalIgnoreCase) && n.Location.Line <= line && line <= n.EndLine).ToArray();
            if (byLocation.Length == 1) candidates = byLocation;
        }
        return candidates.Length switch
        {
            1 => (candidates[0], "resolved"),
            0 => (null, "external"),
            _ => (null, "ambiguous")
        };
    }

    /// <summary>Reduces compiler-generated names (async state machines, lambdas, local functions) to the user-written method.</summary>
    private static (string? Type, string Method) Normalize(string symbol)
    {
        symbol = Regex.Replace(symbol, @"\[[^\]]*\]", "");           // generic arguments: Method[T]
        symbol = Regex.Replace(symbol, @"\s*\(.*$", "");
        var parts = symbol.Replace('+', '.').Split('.');
        if (parts.Length < 2) return (null, symbol);
        var method = parts[^1];
        var typeParts = parts[..^1].ToList();

        // Ns.Type.<Run>d__3.MoveNext  →  Ns.Type.Run
        if (method == "MoveNext" && typeParts.Count > 0 && Regex.Match(typeParts[^1], @"^<(?<m>.+)>d__\d+$") is { Success: true } state)
        {
            method = state.Groups["m"].Value;
            typeParts.RemoveAt(typeParts.Count - 1);
        }
        // Ns.Type.<>c.<Run>b__0_0  or  Ns.Type.<>c__DisplayClass1_0.<Run>b__0  →  Ns.Type.Run
        else if (Regex.Match(method, @"^<(?<m>.+)>b__\d+(_\d+)?$") is { Success: true } lambda)
        {
            method = lambda.Groups["m"].Value;
            while (typeParts.Count > 0 && typeParts[^1].StartsWith("<>c", StringComparison.Ordinal)) typeParts.RemoveAt(typeParts.Count - 1);
        }
        else if (Regex.Match(method, @"^<(?<m>.+)>g__(?<local>.+)\|\d+_\d+$") is { Success: true } local)
        {
            method = local.Groups["m"].Value;
        }
        return typeParts.Count == 0 ? (null, method) : (string.Join('.', typeParts), method);
    }

    private static int ParameterCount(string signature)
    {
        var open = signature.IndexOf('(');
        var inner = open < 0 ? "" : signature[(open + 1)..signature.LastIndexOf(')')].Trim();
        if (inner.Length == 0) return 0;
        var depth = 0; var count = 1;
        foreach (var c in inner) { if (c is '<' or '(') depth++; else if (c is '>' or ')') depth--; else if (c == ',' && depth == 0) count++; }
        return count;
    }

    private static string RelativeTo(string root, string file)
    {
        try
        {
            if (!Path.IsPathRooted(file)) return file.Replace('\\', '/');
            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            return relative.StartsWith("../", StringComparison.Ordinal) ? file.Replace('\\', '/') : relative;
        }
        catch { return file; }
    }
}
