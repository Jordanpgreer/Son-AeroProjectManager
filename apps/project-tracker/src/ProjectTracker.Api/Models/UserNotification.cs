namespace ProjectTracker.Api.Models;

public sealed class UserNotification
{
    public int Id { get; set; }
    public int RecipientUserId { get; set; }
    public AppUser RecipientUser { get; set; } = null!;
    public int ProjectId { get; set; }
    public Project Project { get; set; } = null!;
    public int? ProjectTaskId { get; set; }
    public ProjectTask? ProjectTask { get; set; }
    public int? ProjectMessageId { get; set; }
    public ProjectMessage? ProjectMessage { get; set; }
    public NotificationKind Kind { get; set; }
    public string ActorAccountName { get; set; } = string.Empty;
    public string ActorDisplayName { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string BodyPreview { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ReadAt { get; set; }
}
