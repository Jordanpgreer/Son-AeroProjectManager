namespace ProjectTracker.Api.Models;

public sealed class ProjectNotificationPreference
{
    public int ProjectId { get; set; }
    public Project Project { get; set; } = null!;
    public int AppUserId { get; set; }
    public AppUser User { get; set; } = null!;
    public bool Enabled { get; set; }
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public string? UpdatedByAccountName { get; set; }
}
