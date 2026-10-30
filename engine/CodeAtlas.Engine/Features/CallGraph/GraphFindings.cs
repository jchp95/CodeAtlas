/// <summary>Builds <see cref="Finding"/> values for the graph-based rules with the same identity scheme as the Roslyn rules.</summary>
static class GraphFindings
{
    public static readonly RuleDescriptor[] Descriptors =
    {
        new("ATL3001", "CallCycleAcrossTypes", "architecture", FindingSeverity.Warning, "Call cycle spanning more than one type, proven through statically bound calls."),
        new("ATL3002", "ProjectReferenceCycle", "architecture", FindingSeverity.Error, "Circular ProjectReference chain."),
        new("ATL3003", "LayerCycle", "architecture", FindingSeverity.Warning, "Circular dependency between configured layers."),
        new("ATL3010", "ForbiddenDependency", "architecture", FindingSeverity.Error, "Dependency prohibited by the configured architecture rules."),
        new("ATL3011", "DependencyNotAllowed", "architecture", FindingSeverity.Error, "Dependency outside the configured allow-list of a layer."),
        new("ATL3012", "ModuleBoundaryViolation", "architecture", FindingSeverity.Error, "Call into a module through a namespace that is not part of its public surface."),
        new("ATL3101", "BlockingReachedFromAsync", "async", FindingSeverity.Warning, "An asynchronous member reaches a synchronous wait through a chain of calls."),
        new("ATL3102", "RemoteCallInLoopViaCalls", "performance", FindingSeverity.Warning, "A loop calls a method that performs a network or database operation."),
        new("ATL3103", "CancellationDroppedInChain", "async", FindingSeverity.Warning, "A CancellationToken is lost on the way to a call that could honor it."),
        new("ATL3104", "DisposableFactoryResultNotDisposed", "resources", FindingSeverity.Warning, "A value returned by a disposable factory is never disposed by its caller."),
        new("PRF0001", "HotMethod", "performance", FindingSeverity.Info, "A method of the solution accounts for a large share of sampled CPU in a real run."),
        new("PRF0101", "SustainedHeapGrowth", "memory", FindingSeverity.Warning, "Post-GC heap size grows steadily during the run."),
        new("PRF0102", "HighAllocationRate", "memory", FindingSeverity.Info, "Sustained high allocation rate."),
        new("PRF0103", "TimeInGc", "memory", FindingSeverity.Warning, "A large fraction of the run was spent paused for garbage collection."),
        new("PRF0201", "ThreadPoolPressure", "concurrency", FindingSeverity.Warning, "Thread-pool queue and threads show sustained pressure."),
        new("PRF0202", "LockContention", "concurrency", FindingSeverity.Warning, "Monitor lock contention occurs at a sustained rate."),
        new("PRF0301", "RetainedGrowth", "memory", FindingSeverity.Warning, "Reachable objects of a type grew between two heap snapshots."),
        new("TST0001", "TestFailed", "tests", FindingSeverity.Error, "A test failed in a real run; the result comes from the TRX file."),
        new("TST0002", "TestsNotRun", "tests", FindingSeverity.Warning, "Tests were discovered in source but produced no result in the run."),
        new("ATL4001", "ExceptionTrace", "exceptions", FindingSeverity.Error, "An exception stack trace resolved against the solution."),
        new("ATL3105", "NullFlowFromCompiler", "reliability", FindingSeverity.Info, "The compiler's nullable flow analysis reports a possible null at a call boundary.")
    };

    public static Finding Create(string ruleId, FindingSeverity severity, ValidationLevel validation, string title, string description,
        FindingLocation location, FindingEvidence[] evidence, CallPathStep[]? path, string confidence, string[] limitations,
        string impact, string recommendation, string reproduction, string discriminator,
        FindingOrigin origin = FindingOrigin.Static)
    {
        var descriptor = Descriptors.First(d => d.Id == ruleId);
        return new Finding(
            FindingIdentity.Create(ruleId, location.Project, location.File, location.Symbol, FindingIdentity.Normalize(discriminator), 0),
            ruleId, descriptor.Category, severity, validation, origin, title, description, location, evidence, path, confidence,
            limitations, impact, recommendation, reproduction, null, FindingStatus.New, null,
            new[] { new FindingHistoryEntry(DateTimeOffset.UtcNow.ToString("o"), "detected", $"{ruleId} by CodeAtlas {(origin == FindingOrigin.Static ? "call-graph analysis" : "runtime evidence")}") });
    }

    public static FindingLocation LocationOf(GraphNode node, GraphLocation? at = null)
    {
        var location = at ?? node.Location ?? new GraphLocation("(unknown)", 1);
        return new FindingLocation(location.File, location.Line, 1, location.Line, $"{node.ContainingType}.{node.Signature}".TrimStart('.'), node.Project);
    }
}
