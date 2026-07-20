namespace ProjectTracker.Api.Models;

public sealed class StatusHistory
{
    public int Id { get; set; }
    public int ProjectId { get; set; }
    public Project Project { get; set; } = null!;
    public int? ProjectTaskId { get; set; }
    public ProjectTask? ProjectTask { get; set; }
    public string EntityName { get; set; } = string.Empty;
    public string OldStatus { get; set; } = string.Empty;
    public string NewStatus { get; set; } = string.Empty;
    public string ChangedBy { get; set; } = string.Empty;
    public DateTimeOffset ChangedAt { get; set; } = DateTimeOffset.UtcNow;
}

