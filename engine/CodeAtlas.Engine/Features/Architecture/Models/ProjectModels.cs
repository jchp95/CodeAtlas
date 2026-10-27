sealed record ProjectDefinition(ProjectNode Node, string ProjectFile, BusinessFlow[]? BusinessFlows = null);
sealed record ProjectNode(
    string Id,
    string Name,
    string Path,
    string Kind,
    string? Framework,
    string[] Endpoints,
    string[] Dependencies,
    string[] ProjectReferences,
    string[] ContractsPublished,
    string[] ContractsConsumed,
    string[] PublishedEvidence,
    string[] ConsumedEvidence,
    HttpTargetCandidate[] HttpTargets,
    string[] ListenAddresses,
    ArchitectureEvidence[]? CodeEvidence = null,
    string? RepositoryRoot = null);
sealed record HttpTargetCandidate(string Url, string SourcePath);
sealed record ArchitectureEvidence(string Category, string Symbol, string File, int Line, string Confidence, string Detail);
sealed record ArchitectureRelation(string Id, string SourceId, string TargetId, string Type, string Label, string Evidence, string Confidence, string? Contract = null);
