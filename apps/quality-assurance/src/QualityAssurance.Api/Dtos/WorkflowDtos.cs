namespace QualityAssurance.Api.Dtos;

public sealed record QualityWorkflowGraph(
    string Module, string Name, IReadOnlyList<QualityWorkflowNode> Nodes, IReadOnlyList<QualityWorkflowEdge> Edges);

public sealed record QualityWorkflowNode(
    string Id, string Type, string Label, double X, double Y,
    string? Trigger = null, string? Field = null, string? Operator = null, string? Value = null,
    int? TargetGroupId = null, string? AssignmentMode = null, int? TargetUserId = null,
    IReadOnlyList<int>? AllowedGroupIds = null);

public sealed record QualityWorkflowEdge(string Id, string Source, string Target, string? Branch = null);
public sealed record QualityWorkflowIssue(string? NodeId, string Message);
public sealed record QualityWorkflowValidation(bool IsValid, IReadOnlyList<QualityWorkflowIssue> Issues);
public sealed record QualityWorkflowSaveDto(long Version, QualityWorkflowGraph Graph);
public sealed record QualityWorkflowVersionDto(long Version);
public sealed record QualityWorkflowContext(
    string? Customer = null, string? TaskType = null, string? Status = null, string? HoldReason = null,
    int? AssignedGroupId = null, IReadOnlyList<int>? ActorGroupIds = null);
public sealed record QualityWorkflowSimulationDto(QualityWorkflowGraph Graph, string Trigger, QualityWorkflowContext Context);
public sealed record QualityWorkflowSimulation(
    bool IsValid, IReadOnlyList<QualityWorkflowIssue> Issues, IReadOnlyList<string> Path, string Outcome,
    int? TargetGroupId, int? TargetUserId, string? AssignmentMode, string Message);
public sealed record QualityWorkflowHistoryDto(
    long Id, string Action, int Revision, string AccountName, string DisplayName, DateTimeOffset OccurredAt);
public sealed record QualityWorkflowDto(
    string Module, long Version, int PublishedRevision, DateTimeOffset? PublishedAt, string? PublishedBy,
    DateTimeOffset? UpdatedAt, string? UpdatedBy, QualityWorkflowGraph Draft, QualityWorkflowGraph? Published,
    QualityWorkflowValidation Validation, IReadOnlyList<QualityWorkflowHistoryDto> History);
