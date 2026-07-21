namespace ProjectTracker.Api.Models;

public sealed class AppGroupPermission
{
    public int AppGroupId { get; set; }
    public AppGroup Group { get; set; } = null!;
    public string PermissionKey { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
