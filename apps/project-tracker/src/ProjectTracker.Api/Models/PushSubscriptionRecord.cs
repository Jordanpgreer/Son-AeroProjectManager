namespace ProjectTracker.Api.Models;

public sealed class PushSubscriptionRecord
{
    public int Id { get; set; }
    public int AppUserId { get; set; }
    public string Endpoint { get; set; } = string.Empty;
    public string P256dh { get; set; } = string.Empty;
    public string Auth { get; set; } = string.Empty;
    public DateTimeOffset? ExpirationTime { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public AppUser User { get; set; } = null!;
}
