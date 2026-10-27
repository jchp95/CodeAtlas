sealed record ParsedHttpRequest(int Index, string Name, string Method, string Url, string HeadersText, string Body, int Line,
    Dictionary<string, string> FileVariables);
sealed record HttpEnvironmentInfo(string[] EnvironmentNames, HashSet<string> AllVariableNames, Dictionary<string, string> SharedValues, Dictionary<string, string> SelectedValues);
sealed record ApiEndpoint(string Id, string ProjectId, string ProjectName, string RepositoryPath, string Framework, string Method,
    string Route, string SourcePath, int Line, string? RequestType, string? SampleBodyJson, string SchemaSource,
    string[] PathParameters, string Confidence, string Detail, string[] ListenAddresses);
sealed record HttpTestRequest(string Id, string ProjectId, string ProjectName, string RepositoryPath, string SourcePath,
    int Index, string Name, string Method, string Url, string HeadersText, string Body, int Line,
    string[] AssociatedEndpointIds, string[] UnresolvedVariables, string[] AvailableEnvironments);
sealed record HttpTestingCatalog(string RootPath, ApiEndpoint[] Endpoints, HttpTestRequest[] Requests, HttpCallTrace[] HttpCalls, string[] Warnings);
sealed record HttpCallTrace(string Id, string SourceProjectId, string SourceProjectName, string RepositoryPath,
    string SourcePath, int Line, string Method, string Route, string Symbol, string[] TargetEndpointIds,
    string Confidence, string Detail);
sealed record HttpCallCandidate(string Id, string SourceProjectId, string SourceProjectName, string RepositoryPath,
    string SourcePath, int Line, string Method, string Route, string Symbol);
sealed record HttpRequestOverrides(string? Method, string? Url, string? HeadersText, string? Body);
sealed record HttpExecutionResult(bool Success, int StatusCode, string ReasonPhrase, long DurationMs, string Url,
    Dictionary<string, string> Headers, string Body, bool BodyTruncated, string? Error);
