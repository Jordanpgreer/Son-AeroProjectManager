namespace ProjectTracker.Api.Models;

public sealed class AppUser
{
    public int Id { get; set; }
    public string AccountName { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;
    public DateTimeOffset LastSeenAt { get; set; } = DateTimeOffset.UtcNow;
    public ICollection<AppUserGroupMembership> GroupMemberships { get; set; } = [];
    public ICollection<UserNotification> Notifications { get; set; } = [];
}

