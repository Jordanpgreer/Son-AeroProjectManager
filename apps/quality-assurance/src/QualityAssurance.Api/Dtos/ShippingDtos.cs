using System.Text.Json;

namespace QualityAssurance.Api.Dtos;

public sealed record QualityFieldAccessDto(
    string Key,
    string Label,
    bool CanView,
    bool CanEdit);

public sealed record QualityShipmentDto(
    int Id,
    long Version,
    string? Status,
    string? SalesOrderNumber,
    DateOnly? QaArrivalDate,
    string? PartNumber,
    string? PurchaseOrderNumber,
    string? Customer,
    string? TaskType,
    decimal? Quantity,
    decimal? DollarValue,
    DateOnly? ShipDate,
    string? HoldReason,
    DateOnly? SourceRequestedDate,
    string? NextAction,
    DateTimeOffset? LastWorkedAt,
    string? Comments,
    int? AssignedGroupId,
    string? AssignedGroupName,
    int? AssignedUserId,
    string? AssignedDisplayName,
    bool IsShipped,
    string DueState,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? ShippedAt);

public sealed record QualityShipmentListDto(
    IReadOnlyList<QualityShipmentDto> Items,
    int Total,
    string Status,
    string Scope,
    string Sort,
    IReadOnlyList<QualityFieldAccessDto> Fields);

public sealed record QualityShipmentCreateDto(
    string? Status,
    string? SalesOrderNumber,
    DateOnly? QaArrivalDate,
    string? PartNumber,
    string? PurchaseOrderNumber,
    string? Customer,
    string? TaskType,
    decimal? Quantity,
    decimal? DollarValue,
    DateOnly? ShipDate,
    string? HoldReason,
    DateOnly? SourceRequestedDate,
    string? NextAction,
    string? Comments);

public sealed record QualityShipmentPatchDto(
    long Version,
    IReadOnlyDictionary<string, JsonElement> Changes);

public sealed record QualityShipmentAssignmentDto(
    long Version,
    int? GroupId,
    int? UserId);

public sealed record QualityShipmentVersionDto(long Version);

public sealed record QualityShipmentAuditDto(
    long Id,
    string EventType,
    string? FieldName,
    string? OldValue,
    string? NewValue,
    string AccountName,
    string DisplayName,
    DateTimeOffset OccurredAt);

public sealed record QualityQueueMetricsDto(
    int Open,
    int Overdue,
    int Completed,
    double? AverageCompletionHours);

public sealed record QualityPersonQueueDto(
    int UserId,
    string DisplayName,
    string AccountName,
    QualityQueueMetricsDto Metrics);

public sealed record QualityDashboardDto(
    QualityQueueMetricsDto MyQueue,
    IReadOnlyList<QualityShipmentDto> Queue,
    IReadOnlyList<QualityPersonQueueDto> TeamQueues,
    bool CanViewTeam);

public sealed record QualityDirectoryGroupDto(
    int Id,
    string Name,
    string? Description,
    int ActiveUserCount);

public sealed record QualityDirectoryUserDto(
    int Id,
    string AccountName,
    string DisplayName,
    IReadOnlyList<int> GroupIds);

public sealed record QualityAssignmentOptionsDto(
    IReadOnlyList<QualityDirectoryGroupDto> Groups,
    IReadOnlyList<QualityDirectoryUserDto> Users);

public sealed record QualityAssignmentRuleDto(
    int Id,
    string Name,
    bool IsEnabled,
    int Priority,
    string MatchField,
    string MatchOperator,
    string MatchValue,
    int TargetGroupId,
    string TargetGroupName,
    string AssignmentMode,
    int? TargetUserId,
    string? TargetDisplayName,
    long Version,
    DateTimeOffset UpdatedAt,
    string UpdatedBy);

public sealed record QualityAssignmentRuleUpsertDto(
    string Name,
    bool IsEnabled,
    int Priority,
    string MatchField,
    string MatchOperator,
    string MatchValue,
    int TargetGroupId,
    string AssignmentMode,
    int? TargetUserId,
    long? Version);

public sealed record QualityShippingColumnDto(
    string Key,
    int Width,
    bool IsVisible);

public sealed record QualityShippingLayoutDto(
    IReadOnlyList<QualityShippingColumnDto> Columns,
    long Version,
    DateTimeOffset? UpdatedAt);

public sealed record QualityShippingLayoutUpdateDto(
    IReadOnlyList<QualityShippingColumnDto> Columns,
    long Version);
