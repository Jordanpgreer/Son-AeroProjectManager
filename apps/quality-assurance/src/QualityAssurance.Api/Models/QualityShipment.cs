namespace QualityAssurance.Api.Models;

public sealed class QualityShipment
{
    public int Id { get; set; }
    public string Status { get; set; } = "WIP";
    public string SalesOrderNumber { get; set; } = string.Empty;
    public DateOnly? QaArrivalDate { get; set; }
    public string PartNumber { get; set; } = string.Empty;
    public string? PurchaseOrderNumber { get; set; }
    public string Customer { get; set; } = string.Empty;
    public string TaskType { get; set; } = "General";
    public decimal? Quantity { get; set; }
    public decimal? DollarValue { get; set; }
    public DateOnly? ShipDate { get; set; }
    public string? HoldReason { get; set; }
    public DateOnly? SourceRequestedDate { get; set; }
    public string? NextAction { get; set; }
    public string? LegacyAssigneeTag { get; set; }
    public string? Comments { get; set; }
    public DateTimeOffset? LastWorkedAt { get; set; }
    public int? AssignedGroupId { get; set; }
    public string? AssignedGroupName { get; set; }
    public int? AssignedUserId { get; set; }
    public string? AssignedAccountName { get; set; }
    public string? AssignedDisplayName { get; set; }
    public bool IsShipped { get; set; }
    public DateTimeOffset? ShippedAt { get; set; }
    public string? ShippedByAccountName { get; set; }
    public string? ShippedByDisplayName { get; set; }
    public string? ExternalShipmentId { get; set; }
    public string? ExternalShipmentUrl { get; set; }
    public string? ExternalShipmentStatus { get; set; }
    public string? ExternalSyncProvider { get; set; }
    public string? ExternalSyncError { get; set; }
    public DateTimeOffset? ExternalSyncedAt { get; set; }
    public long Version { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public string CreatedByAccountName { get; set; } = string.Empty;
    public string CreatedByDisplayName { get; set; } = string.Empty;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public string UpdatedByAccountName { get; set; } = string.Empty;
    public string UpdatedByDisplayName { get; set; } = string.Empty;
    public ICollection<QualityShipmentAuditEntry> AuditEntries { get; set; } = [];
    public ICollection<QualityShipmentComment> CommentThread { get; set; } = [];
    public ICollection<QualityShipmentPart> Parts { get; set; } = [];
}

public sealed class QualityShipmentPart
{
    public int Id { get; set; }
    public int ShipmentId { get; set; }
    public QualityShipment Shipment { get; set; } = null!;
    public string PartNumber { get; set; } = string.Empty;
    public int? Quantity { get; set; }
    public decimal? UnitPrice { get; set; }
    public decimal? TotalValue { get; set; }
    public string? ExternalItemId { get; set; }
    public int DisplayOrder { get; set; }
}

public sealed class QualityShipmentAuditEntry
{
    public long Id { get; set; }
    public int ShipmentId { get; set; }
    public QualityShipment Shipment { get; set; } = null!;
    public string EventType { get; set; } = string.Empty;
    public string? FieldName { get; set; }
    public string? OldValue { get; set; }
    public string? NewValue { get; set; }
    public string AccountName { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public DateTimeOffset OccurredAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class QualityAssignmentRule
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public bool IsEnabled { get; set; } = true;
    public int Priority { get; set; }
    public string MatchField { get; set; } = "Customer";
    public string MatchOperator { get; set; } = "Equals";
    public string MatchValue { get; set; } = string.Empty;
    public int TargetGroupId { get; set; }
    public string TargetGroupName { get; set; } = string.Empty;
    public string AssignmentMode { get; set; } = "GroupOnly";
    public int? TargetUserId { get; set; }
    public string? TargetAccountName { get; set; }
    public string? TargetDisplayName { get; set; }
    public long Version { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public string CreatedBy { get; set; } = string.Empty;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public string UpdatedBy { get; set; } = string.Empty;
}

public sealed class QualityShippingLayoutPreference
{
    public int Id { get; set; }
    public int AppUserId { get; set; }
    public string AccountName { get; set; } = string.Empty;
    public string LayoutJson { get; set; } = string.Empty;
    public long Version { get; set; }
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
