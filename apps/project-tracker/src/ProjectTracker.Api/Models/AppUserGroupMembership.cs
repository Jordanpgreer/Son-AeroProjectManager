namespace ProjectTracker.Api.Models;

public sealed class AppUserGroupMembership
{
    public int AppUserId { get; set; }
    public AppUser User { get; set; } = null!;
    public int AppGroupId { get; set; }
    public AppGroup Group { get; set; } = null!;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
