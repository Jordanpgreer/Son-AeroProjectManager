namespace ProjectTracker.Api.Models;

public sealed class Project
{
    public int Id { get; set; }
    public string ProgramName { get; set; } = string.Empty;
    public string? ProgramManager { get; set; }
    public DateOnly? ProgramStart { get; set; }
    public DateOnly? TargetDelivery { get; set; }
    public decimal Progress { get; set; }
    public ProjectStatus Status { get; set; } = ProjectStatus.NotStarted;
    public string? CurrentTask { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public List<ProjectTask> Tasks { get; set; } = [];
}

