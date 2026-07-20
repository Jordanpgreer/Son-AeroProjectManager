namespace ProjectTracker.Api.Models;

public sealed class ProjectAuditEntry
{
    public int Id { get; set; }
    public int ProjectId { get; set; }
    public Project Project { get; set; } = null!;
    public int? ProjectTaskId { get; set; }
    public string Action { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;
    public string ChangesJson { get; set; } = "[]";
    public string ChangedByAccountName { get; set; } = string.Empty;
    public string ChangedByDisplayName { get; set; } = string.Empty;
    public DateTimeOffset ChangedAt { get; set; } = DateTimeOffset.UtcNow;
}
