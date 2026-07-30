namespace ProjectTracker.Api.Models;

public sealed class AppUserModuleAccess
{
    public int AppUserId { get; set; }
    public AppUser User { get; set; } = null!;
    public string ModuleKey { get; set; } = string.Empty;
    public string? Role { get; set; }
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
