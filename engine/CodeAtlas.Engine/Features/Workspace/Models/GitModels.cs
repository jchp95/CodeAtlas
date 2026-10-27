sealed record GitChange(string Status, string Path);
sealed record GitRepositoryState(string Path, string Branch, string HeadCommit, GitChange[]? Changes = null);
sealed record ScanSummary(int DotnetProjects, int NodeProjects, int EndpointCandidates, int ChangedFiles, int Relations, int PublishedContractCandidates, int ConsumedContractCandidates);
