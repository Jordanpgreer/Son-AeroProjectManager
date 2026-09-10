namespace QualityAssurance.Api.Models;

public sealed class QualityWorkflow
{
    public int Id { get; set; }
    public string Module { get; set; } = "quality-assurance";
    public string DraftJson { get; set; } = string.Empty;
    public string? PublishedJson { get; set; }
    public long Version { get; set; }
    public int PublishedRevision { get; set; }
    public DateTimeOffset? PublishedAt { get; set; }
    public string? PublishedBy { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public string UpdatedBy { get; set; } = string.Empty;
}

public sealed class QualityWorkflowAuditEntry
{
    public long Id { get; set; }
    public string Module { get; set; } = "quality-assurance";
    public string Action { get; set; } = string.Empty;
    public int Revision { get; set; }
    public string GraphJson { get; set; } = string.Empty;
    public string AccountName { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public DateTimeOffset OccurredAt { get; set; }
}
