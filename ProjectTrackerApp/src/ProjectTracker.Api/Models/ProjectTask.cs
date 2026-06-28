namespace ProjectTracker.Api.Models;

public sealed class ProjectTask
{
    public int Id { get; set; }
    public int ProjectId { get; set; }
    public Project Project { get; set; } = null!;
    public int Sequence { get; set; }
    public string? ExternalTaskId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string? Phase { get; set; }
    public string? WorkStation { get; set; }
    public int? DependencyTaskId { get; set; }
    public DateOnly? StartDate { get; set; }
    public bool StartDateLocked { get; set; }
    public DateOnly? OriginalStartDate { get; set; }
    public DateOnly? EndDate { get; set; }
    public DateOnly? OriginalEndDate { get; set; }
    public int? EstimatedDuration { get; set; }
    public int? ActualDuration { get; set; }
    public decimal PercentComplete { get; set; }
    public bool PercentCompleteManual { get; set; }
    public TaskScheduleStatus Status { get; set; } = TaskScheduleStatus.NotStarted;
    public string? Notes { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public List<TaskOvertimeDay> OvertimeDays { get; set; } = [];
}

