sealed record ProfileTarget(string? Program, string[] Arguments, int? AttachProcessId);

sealed record ToolStatus(string Name, string? Path, string? Version, bool Available);

sealed record CpuMethod(
    string Symbol,
    string? NodeId,
    string Resolution,
    GraphLocation? Location,
    double OwnCpuMs,
    double OwnShare,
    double ExclusiveCpuMs,
    double InclusiveCpuMs,
    double InclusiveShare,
    double BlockedMs,
    double MinOwnShare,
    double MaxOwnShare,
    int RunsPresent,
    string[] StaticFindingIds);

sealed record CallTreeNode(string Symbol, string? NodeId, double InclusiveCpuMs, double Share, CallTreeNode[] Children);

sealed record CpuRun(int Index, string TraceFile, double SpanMs, double CpuMs, double BlockedMs, int Threads, double ApproximateIntervalMs, string Status);

sealed record CpuProfileReport(
    int SchemaVersion,
    string Kind,
    string RepositoryPath,
    string GeneratedAt,
    string Status,
    ProfileTarget Target,
    string[] Commands,
    CpuRun[] Runs,
    CpuMethod[] Methods,
    CallTreeNode? CallTree,
    Finding[] Findings,
    string[] Limitations,
    string[] Notes);

sealed record SeriesSummary(string Name, string Unit, int Samples, double Min, double Mean, double P95, double Max, double Last, double SlopePerSecond, double RSquared, double RisingFraction);

sealed record CounterProfileReport(
    int SchemaVersion,
    string Kind,
    string RepositoryPath,
    string GeneratedAt,
    string Status,
    ProfileTarget Target,
    string Command,
    double DurationSeconds,
    int RefreshSeconds,
    SeriesSummary[] Series,
    Dictionary<string, double> Totals,
    Finding[] Findings,
    string[] Limitations,
    string[] Notes);

sealed record HeapType(string Type, long Bytes, long Count);

sealed record HeapSnapshot(string File, long TotalBytes, long TotalObjects, long AccountedBytes, HeapType[] Types);

sealed record HeapGrowth(string Type, long BytesBefore, long BytesAfter, long CountBefore, long CountAfter, long GrowthBytes, string? SourceNodeId);

sealed record HeapProfileReport(
    int SchemaVersion,
    string Kind,
    string RepositoryPath,
    string GeneratedAt,
    string Status,
    ProfileTarget Target,
    string[] Commands,
    HeapSnapshot[] Snapshots,
    HeapGrowth[] Growth,
    Finding[] Findings,
    string[] Limitations,
    string[] Notes);

sealed record ProfileDelta(string Subject, double Before, double After, double ChangePercent, string Verdict, string Reason);

sealed record ProfileComparison(int SchemaVersion, string Kind, string Baseline, string Current, string Verdict, ProfileDelta[] Deltas, string[] Notes);
