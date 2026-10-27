internal sealed record BusinessFlow(
    string Id,
    string ProjectId,
    string ProjectName,
    string Name,
    string EntryPoint,
    string EntryFile,
    int EntryLine,
    string Confidence,
    string Summary,
    BusinessFlowStep[] Steps);

internal sealed record BusinessFlowStep(
    int Sequence,
    string Kind,
    string Symbol,
    string File,
    int Line,
    string Confidence,
    string Detail);

internal sealed record FlowChange(
    string Id,
    string Status,
    string ProjectId,
    string ProjectName,
    string Name,
    string[] Changes,
    BusinessFlowStep[] PreviousSteps,
    BusinessFlowStep[] CurrentSteps);

internal sealed record DistributedFlow(
    string Id,
    string Contract,
    string ProducerProjectId,
    string ProducerProjectName,
    string? ProducerFlowId,
    string ProducerEntryPoint,
    string ConsumerProjectId,
    string ConsumerProjectName,
    string? ConsumerFlowId,
    string ConsumerEntryPoint,
    string Confidence,
    string Summary,
    DistributedFlowStep[] Steps);

internal sealed record DistributedFlowStep(
    int Sequence,
    string Phase,
    string ProjectId,
    string ProjectName,
    string Kind,
    string Symbol,
    string File,
    int Line,
    string Confidence,
    string Detail);

internal sealed record DistributedFlowChange(
    string Id,
    string Status,
    string Contract,
    string ProducerProjectName,
    string ConsumerProjectName,
    string ProducerEntryPoint,
    string ConsumerEntryPoint,
    string[] Changes);
