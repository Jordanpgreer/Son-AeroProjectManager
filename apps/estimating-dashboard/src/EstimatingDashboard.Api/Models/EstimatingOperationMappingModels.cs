namespace EstimatingDashboard.Api.Models;

public sealed class EstimatingRateReferenceRecord
{
    public string Key { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public int SourceRow { get; set; }
    public string OperationName { get; set; } = string.Empty;
    public bool IsActive { get; set; }
    public ICollection<EstimatingOperationMappingRecord> OperationMappings { get; set; } = [];
}

public sealed class EstimatingOperationMappingRecord
{
    public int Id { get; set; }
    public string FulcrumOperation { get; set; } = string.Empty;
    public string FulcrumOperationKey { get; set; } = string.Empty;
    public string RateReferenceKey { get; set; } = string.Empty;
    public EstimatingRateReferenceRecord RateReference { get; set; } = null!;
    public bool IsActive { get; set; }
    public int Version { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public string CreatedBy { get; set; } = string.Empty;
    public DateTimeOffset UpdatedAt { get; set; }
    public string UpdatedBy { get; set; } = string.Empty;
    public ICollection<EstimatingOperationMappingAuditRecord> AuditHistory { get; set; } = [];
}

public sealed class EstimatingOperationMappingAuditRecord
{
    public long Id { get; set; }
    public int OperationMappingId { get; set; }
    public EstimatingOperationMappingRecord OperationMapping { get; set; } = null!;
    public string Action { get; set; } = string.Empty;
    public string? OldFulcrumOperation { get; set; }
    public string? NewFulcrumOperation { get; set; }
    public string? OldRateReferenceKey { get; set; }
    public string? NewRateReferenceKey { get; set; }
    public bool? OldIsActive { get; set; }
    public bool? NewIsActive { get; set; }
    public DateTimeOffset ChangedAt { get; set; }
    public string ChangedBy { get; set; } = string.Empty;
}

public static class EstimatingOperationMappingAuditActions
{
    public const string Created = "Created";
    public const string Updated = "Updated";
    public const string Deactivated = "Deactivated";
}
