enum TestOutcome { Passed, Failed, Skipped, NotExecuted, Other }

/// <summary>A test found in source (not executed). <c>Cases</c> is the number of data rows declared by attributes; null when unknown.</summary>
sealed record DiscoveredTest(string NodeId, string Project, string Class, string Method, string Framework, bool Skipped, string? SkipReason, int? Cases,
    GraphLocation? Location);

sealed record TestDiscoveryReport(int SchemaVersion, string Source, DiscoveredTest[] Tests, string[] TestProjects, GraphCompleteness GraphCompleteness, string[] Notes);

sealed record StackFrame(string Symbol, string? File, int? Line, string? NodeId, string Resolution);

sealed record TestResult(
    string Name,
    string Class,
    string Method,
    string Project,
    TestOutcome Outcome,
    string RawOutcome,
    double DurationMs,
    string? Message,
    string? StackTrace,
    StackFrame[] Frames);

sealed record TestRunProject(
    string Project,
    string ProjectFile,
    string Status,
    string Command,
    int? ExitCode,
    double DurationMs,
    string? TrxFile,
    string? CoverageFile,
    TestResult[] Results,
    string[] OutputTail);

sealed record TestRunSummary(int Passed, int Failed, int Skipped, int NotExecuted, int Other, double DurationMs);

sealed record CoveredFinding(string FindingId, string RuleId, string File, int Line, string Coverage);

sealed record CoverageSummary(string File, double LineRate, int LinesCovered, int LinesValid, CoveredFinding[] Findings);

sealed record TestRunReport(
    int SchemaVersion,
    string RepositoryPath,
    string GeneratedAt,
    string Status,
    string? Filter,
    string ResultsDirectory,
    TestRunProject[] Projects,
    TestRunSummary Summary,
    string[] NotRun,
    CoverageSummary? Coverage,
    Finding[] Findings,
    string[] Notes);

sealed record TestChange(string Test, string Project, string Before, string After, double? DurationDeltaMs);

sealed record TestComparison(int SchemaVersion, string Baseline, string Current, TestChange[] Regressions, TestChange[] Fixed, TestChange[] Added,
    TestChange[] Removed, TestChange[] DurationChanges, string[] Notes);
