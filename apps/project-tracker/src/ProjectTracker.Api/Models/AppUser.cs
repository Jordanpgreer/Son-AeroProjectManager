namespace ProjectTracker.Api.Models;

public sealed class AppUser
{
    public int Id { get; set; }
    public string AccountName { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Role { get; set; } = "Viewer";
    public DateTimeOffset LastSeenAt { get; set; } = DateTimeOffset.UtcNow;
}

