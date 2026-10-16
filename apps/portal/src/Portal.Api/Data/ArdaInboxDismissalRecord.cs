using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Portal.Api.Data;

/// <summary>Recipient-owned inbox state; source workflow records remain intact.</summary>
public sealed class ArdaInboxDismissalRecord
{
    public string AccountName { get; set; } = string.Empty;
    public string SourceKind { get; set; } = string.Empty;
    public long SourceNotificationId { get; set; }
    public DateTimeOffset DismissedAt { get; set; }
}

public sealed class ArdaInboxDismissalConfiguration : IEntityTypeConfiguration<ArdaInboxDismissalRecord>
{
    public void Configure(EntityTypeBuilder<ArdaInboxDismissalRecord> entity)
    {
        entity.ToTable("ArdaInboxDismissals");
        entity.HasKey(item => new { item.AccountName, item.SourceKind, item.SourceNotificationId });
        entity.Property(item => item.AccountName).HasMaxLength(160);
        entity.Property(item => item.SourceKind).HasMaxLength(32);
        entity.HasIndex(item => new { item.SourceKind, item.SourceNotificationId });
    }
}
