namespace QualityAssurance.Api.Models;

public sealed class QualityTeam
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public bool IsActive { get; set; } = true;
    public long Version { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public string CreatedBy { get; set; } = string.Empty;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public string UpdatedBy { get; set; } = string.Empty;
    public ICollection<QualityTeamMember> Members { get; set; } = [];
}

public sealed class QualityTeamMember
{
    public int QualityTeamId { get; set; }
    public QualityTeam Team { get; set; } = null!;
    public int AppUserId { get; set; }
    public string AccountName { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public bool IsManager { get; set; }
    public DateTimeOffset AddedAt { get; set; } = DateTimeOffset.UtcNow;
    public string AddedBy { get; set; } = string.Empty;
}
