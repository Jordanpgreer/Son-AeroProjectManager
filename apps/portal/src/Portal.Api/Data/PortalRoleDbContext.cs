using Microsoft.EntityFrameworkCore;

namespace Portal.Api.Data;

public sealed class PortalRoleDbContext(DbContextOptions<PortalRoleDbContext> options) : DbContext(options)
{
    public DbSet<PortalRoleRecord> Users => Set<PortalRoleRecord>();
    public DbSet<PortalNotificationRecord> UserNotifications => Set<PortalNotificationRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<PortalRoleRecord>(entity =>
        {
            entity.ToTable("Users");
            entity.HasKey(user => user.Id);
            entity.Property(user => user.AccountName).HasMaxLength(160);
            entity.Property(user => user.DisplayName).HasMaxLength(160);
            entity.Property(user => user.Role).HasMaxLength(32);
        });

        modelBuilder.Entity<PortalNotificationRecord>(entity =>
        {
            entity.ToTable("UserNotifications");
            entity.HasKey(notification => notification.Id);
            entity.HasIndex(notification => new
            {
                notification.RecipientUserId,
                notification.ReadAt,
                notification.CreatedAt
            });
        });
    }
}

public sealed class PortalRoleRecord
{
    public int Id { get; set; }
    public string AccountName { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Role { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;
    public DateTimeOffset LastSeenAt { get; set; }
}

public sealed class PortalNotificationRecord
{
    public int Id { get; set; }
    public int RecipientUserId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ReadAt { get; set; }
}
