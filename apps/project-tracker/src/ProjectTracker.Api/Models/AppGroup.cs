namespace ProjectTracker.Api.Models;

public sealed class AppGroup
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public bool IsSystemGroup { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public ICollection<AppUserGroupMembership> UserMemberships { get; set; } = [];
    public ICollection<AppGroupPermission> Permissions { get; set; } = [];
}
