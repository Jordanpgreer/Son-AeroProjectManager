namespace ProjectTracker.Api.Models;

public sealed class TaskOvertimeDay
{
    public int Id { get; set; }
    public int ProjectTaskId { get; set; }
    public ProjectTask ProjectTask { get; set; } = null!;
    public DateOnly Date { get; set; }
    public string? Note { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
