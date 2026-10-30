using System.Text.RegularExpressions;

sealed record ExceptionSegment(string Type, string? Message, bool IsInner, StackFrame[] Frames);

sealed record ExceptionAnalysis(
    int SchemaVersion,
    string Status,
    ExceptionSegment[] Segments,
    StackFrame? OriginFrame,
    NodeSummary? OriginNode,
    ImpactItem[] Callers,
    ImpactFlow[] Flows,
    ImpactItem[] RelatedTests,
    Finding[] HandlingFindings,
    Finding[] Findings,
    GraphCompleteness GraphCompleteness,
    string[] Notes);

/// <summary>
/// Turns a pasted .NET stack trace into an investigation: where it was thrown, who reaches that code, which tests and entry points
/// exercise it, and whether catch blocks on the path swallow or reset it. Only what the trace and the code prove is reported:
/// variable values and the operation in progress are not in a stack trace.
/// </summary>
static partial class ExceptionAnalyzer
{
    [GeneratedRegex(@"^(?<type>[A-Za-z_][\w.`+<>\[\]]*)(?::\s*(?<msg>.*))?$")]
    private static partial Regex HeaderRegex();

    public static ExceptionAnalysis Analyze(string root, GraphIndex index, string trace, GraphLimits limits)
    {
        var segments = Parse(trace, index, root);
        if (segments.Count == 0)
            return new ExceptionAnalysis(1, "noFrames", Array.Empty<ExceptionSegment>(), null, null, Array.Empty<ImpactItem>(), Array.Empty<ImpactFlow>(), Array.Empty<ImpactItem>(),
                Array.Empty<Finding>(), Array.Empty<Finding>(), index.Graph.Completeness, new[] { "The text contains no stack frames ('   at Namespace.Type.Method(...)')." });

        // The origin is the innermost exception's first frame that belongs to this solution.
        var innermost = segments[^1];
        var origin = innermost.Frames.FirstOrDefault(f => f.NodeId is not null) ?? segments.SelectMany(s => s.Frames).FirstOrDefault(f => f.NodeId is not null);
        var notes = new List<string> { "A stack trace shows the call path at the moment of the throw, not variable values or the business operation in progress." };

        if (origin?.NodeId is null)
        {
            notes.Add("No frame matched a member of the analyzed projects (framework-only trace, other solution, or ambiguous names).");
            return new ExceptionAnalysis(1, "unresolved", segments.ToArray(), null, null, Array.Empty<ImpactItem>(), Array.Empty<ImpactFlow>(), Array.Empty<ImpactItem>(), Array.Empty<Finding>(),
                Array.Empty<Finding>(), index.Graph.Completeness, notes.ToArray());
        }

        var node = index.Nodes[origin.NodeId];
        var impact = ImpactAnalyzer.Analyze(index, node.Id, limits, null);
        var files = segments.SelectMany(s => s.Frames).Select(f => f.File).Where(f => f is not null).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var handling = files.Count == 0 ? Array.Empty<Finding>()
            : new CodeAnalysisEngine(root, new AnalysisOptions(true, new[] { "ATL1004", "ATL1005" })).Run(CancellationToken.None).Findings.Where(f => files.Contains(f.Location.File)).ToArray();
        if (handling.Length > 0) notes.Add($"{handling.Length} catch block(s) in the files of this trace swallow or rethrow-by-value (ATL1004/ATL1005); check whether they sit on this path.");

        var header = segments[^1];
        var path = segments.SelectMany(s => s.Frames).Where(f => f.NodeId is not null).Select(f => index.Step(index.Nodes[f.NodeId!], "direct", f.File is null || f.Line is null ? null : new GraphLocation(f.File, f.Line.Value))).ToArray();
        var location = origin.File is not null && origin.Line is not null
            ? new FindingLocation(origin.File, origin.Line.Value, 1, origin.Line.Value, $"{node.ContainingType}.{node.Signature}", node.Project)
            : GraphFindings.LocationOf(node);
        var finding = GraphFindings.Create("ATL4001", FindingSeverity.Error, ValidationLevel.RuntimeObserved, $"{header.Type} thrown in {node.ContainingType}.{node.Name}",
            header.Message ?? "No message.", location,
            segments.SelectMany(s => s.Frames).Take(8).Select(f => new FindingEvidence("stack-frame", $"{f.Symbol} [{f.Resolution}]", f.File is null || f.Line is null ? null : new FindingLocation(f.File, f.Line.Value, 1, f.Line.Value, null, node.Project))).ToArray(),
            path, origin.File is not null ? "high" : "medium",
            new[] { "The trace is evidence of one occurrence; whether it recurs and under which inputs is unknown.", "Frames without file and line were matched to source by type, name and arity only." },
            $"{impact.DirectReferences.Length} direct caller(s) and {impact.BusinessFlows.Length} entry point(s) reach this code.",
            $"Reproduce with the inputs of that occurrence in a test of {node.ContainingType}.{node.Name}, fix the cause (not by hiding the exception), and keep the test.",
            impact.RelatedTests.Length > 0 ? $"Run the related tests: {string.Join(", ", impact.RelatedTests.Take(3).Select(t => t.Node.Symbol))}" : "No existing test reaches this member: write one that triggers the failing input.",
            $"{header.Type}|{node.Id}", FindingOrigin.Dynamic);

        if (impact.RelatedTests.Length == 0) notes.Add("No test reaches the origin member through the call graph.");
        return new ExceptionAnalysis(1, "resolved", segments.ToArray(), origin, impact.Symbol, impact.DirectReferences, impact.BusinessFlows, impact.RelatedTests, handling,
            new[] { finding }, index.Graph.Completeness, notes.ToArray());
    }

    private static List<ExceptionSegment> Parse(string trace, GraphIndex index, string root)
    {
        var segments = new List<(string Type, string? Message, bool Inner, List<string> Frames)>();
        var stack = new Stack<int>();
        var collectingMessage = false;
        foreach (var rawLine in trace.Replace("\r", "").Split('\n'))
        {
            var line = rawLine.TrimEnd();
            var trimmed = line.TrimStart();
            if (trimmed.Length == 0) continue;
            if (trimmed.StartsWith("--- End of inner exception stack trace ---", StringComparison.Ordinal)) { if (stack.Count > 1) stack.Pop(); collectingMessage = false; continue; }
            if (trimmed.StartsWith("--- End of stack trace from previous location", StringComparison.Ordinal)) continue;
            if (trimmed.StartsWith("at ", StringComparison.Ordinal))
            {
                if (stack.Count == 0) { segments.Add(("(unknown)", null, false, new List<string>())); stack.Push(0); }
                segments[stack.Peek()].Frames.Add(line);
                collectingMessage = false;
                continue;
            }
            var inner = trimmed.StartsWith("--->", StringComparison.Ordinal);
            var match = HeaderRegex().Match(inner ? trimmed[4..].TrimStart() : trimmed);
            if (match.Success && (inner || stack.Count == 0 || match.Groups["type"].Value.Contains('.')))
            {
                segments.Add((match.Groups["type"].Value, match.Groups["msg"].Success ? match.Groups["msg"].Value : null, inner, new List<string>()));
                stack.Push(segments.Count - 1);
                collectingMessage = true;
            }
            else if (collectingMessage && stack.Count > 0)
            {
                var (type, message, isInner, frames) = segments[stack.Peek()];
                segments[stack.Peek()] = (type, (message is null ? "" : message + "\n") + trimmed, isInner, frames);
            }
        }
        // Frames of an inner exception are listed before the "End of inner" marker; segments are ordered outer → inner.
        return segments.Where(s => s.Frames.Count > 0 || s.Type != "(unknown)")
            .Select(s => new ExceptionSegment(s.Type, s.Message, s.Inner, StackTraceResolver.Resolve(string.Join('\n', s.Frames), index, root))).ToList();
    }
}
