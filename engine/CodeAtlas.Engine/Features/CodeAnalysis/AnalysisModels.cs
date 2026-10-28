sealed record RuleDescriptor(
    string Id, 
    string Name, 
    string Category, 
    FindingSeverity DefaultSeverity, 
    string Summary);

sealed record RuleTiming(
    string RuleId, 
    double Milliseconds, 
    int Findings);

sealed record AnalysisTelemetry(
    double TotalMilliseconds,
    double CompilationMilliseconds,
    int ProjectsAnalyzed,
    int ProjectsFromCache,
    int DocumentsAnalyzed,
    long WorkingSetMegabytes,
    int MaxParallelism,
    RuleTiming[] Rules);

sealed record AnalysisProjectSummary(
    string Project, 
    string File, 
    int Documents, 
    int Findings, 
    bool FromCache, 
    string? CacheKey);

sealed record AnalysisReport(
    int SchemaVersion,
    string RepositoryPath,
    string GeneratedAt,
    string RuleSetVersion,
    AnalysisProjectSummary[] Projects,
    Finding[] Findings,
    RuleDescriptor[] Rules,
    string[] Warnings,
    bool Cancelled,
    AnalysisTelemetry Telemetry);
