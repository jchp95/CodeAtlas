/// <summary>How a call site was resolved. Anything other than <c>Direct</c>/<c>ExtensionMethod</c>/<c>ObjectCreation</c>/<c>PropertyAccess</c> describes a set of possible targets, not one proven callee.</summary>
enum EdgeResolution { Direct, InterfaceDispatch, VirtualDispatch, Delegate, Lambda, ExtensionMethod, ObjectCreation, PropertyAccess, Unresolved }

/// <summary>
/// <c>Proven</c>: the call site invokes exactly this member (or, for dispatch, this declared member).
/// <c>Potential</c>: this is one of the known targets a dispatch may reach; other targets may exist.
/// <c>Unknown</c>: no target could be determined.
/// </summary>
enum EdgeCertainty { Proven, Potential, Unknown }

enum GraphStatus { Completed, Cancelled, LimitReached }

sealed record GraphLocation(string File, int Line);

sealed record GraphNode(
    string Id,
    string SymbolId,
    string Kind,
    string Project,
    string Name,
    string Signature,
    string? ContainingType,
    string? Namespace,
    string Accessibility,
    bool IsExternal,
    bool IsAbstract,
    bool IsVirtual,
    string[] Tags,
    GraphLocation? Location,
    GraphLocation[]? OtherLocations = null,
    int EndLine = 0);

sealed record GraphEdge(
    string Id,
    string From,
    string? To,
    EdgeResolution Resolution,
    EdgeCertainty Certainty,
    string SiteId,
    GraphLocation Site,
    bool InLoop,
    bool InTry,
    bool PassesToken,
    bool TargetAcceptsToken,
    bool OpenWorld,
    bool Truncated,
    string? Label,
    string? Reason);

sealed record GraphEntryPoint(string Kind, string Label, string NodeId, string File, int Line, string? Contract);

sealed record GraphLimitNote(string Limit, int Value, string Effect);

sealed record GraphCompleteness(
    bool Closed,
    int UnresolvedCallSites,
    int DelegateInvocations,
    int OpenWorldDispatchSites,
    int TruncatedDispatchSites,
    string Statement);

sealed record GraphMetrics(
    int Projects,
    int Nodes,
    int SourceNodes,
    int ExternalNodes,
    int Edges,
    int EntryPoints,
    int ProjectsFromCache,
    double TotalMilliseconds,
    double CompilationMilliseconds,
    double ExtractionMilliseconds,
    double AssemblyMilliseconds);

/// <summary>Contract version 1. Additive changes keep the version; renamed or removed fields bump it.</summary>
sealed record CallGraph(
    int SchemaVersion,
    string RepositoryPath,
    string GeneratedAt,
    string EngineVersion,
    GraphStatus Status,
    GraphCompleteness Completeness,
    string[] Projects,
    string[][] ProjectReferences,
    GraphNode[] Nodes,
    GraphEdge[] Edges,
    GraphEntryPoint[] EntryPoints,
    GraphLimitNote[] LimitsApplied,
    GraphMetrics Metrics,
    string[] Warnings);

sealed record GraphLimits(int MaxDispatchTargets = 25, int MaxNodes = 250_000, int MaxEdges = 2_000_000, int MaxDepth = 8, int MaxPaths = 5, int MaxResults = 500);

// ---- per-project fragments (cached independently; global dispatch is resolved when fragments are assembled) ----

sealed record RawEdge(
    string From,
    string? Target,
    EdgeResolution Resolution,
    EdgeCertainty Certainty,
    string SiteId,
    GraphLocation Site,
    bool InLoop,
    bool InTry,
    bool PassesToken,
    bool TargetAcceptsToken,
    bool OpenWorld,
    string? ReceiverTypeId,
    string? Label,
    string? Reason);

/// <summary>Interface member (or base virtual member) → implementing/overriding method. <c>OverriderTypes</c> is the chain of the implementer's type and its bases.</summary>
sealed record DispatchPair(string Declared, string Implementation, string[] ImplementerTypes);

sealed record ProjectFragment(
    string Project,
    GraphNode[] Nodes,
    RawEdge[] Edges,
    DispatchPair[] Implementations,
    DispatchPair[] Overrides,
    GraphEntryPoint[] EntryPoints,
    string[] Warnings);
