using Microsoft.EntityFrameworkCore;

namespace Portal.Api.Data;

public sealed class PortalRoleDbContext(DbContextOptions<PortalRoleDbContext> options) : DbContext(options)
{
    public DbSet<PortalRoleRecord> Users => Set<PortalRoleRecord>();
    public DbSet<PortalModuleAccessRecord> UserModuleAccess => Set<PortalModuleAccessRecord>();
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

        modelBuilder.Entity<PortalModuleAccessRecord>(entity =>
        {
            entity.ToTable("UserModuleAccess");
            entity.HasKey(access => new { access.AppUserId, access.ModuleKey });
            entity.Property(access => access.ModuleKey).HasMaxLength(64);
            entity.Property(access => access.Role).HasMaxLength(32);
            entity.HasOne(access => access.User)
                .WithMany(user => user.ModuleAccessAssignments)
                .HasForeignKey(access => access.AppUserId)
                .OnDelete(DeleteBehavior.Cascade);
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
    public ICollection<PortalModuleAccessRecord> ModuleAccessAssignments { get; set; } = [];
}

public sealed class PortalModuleAccessRecord
{
    public int AppUserId { get; set; }
    public string ModuleKey { get; set; } = string.Empty;
    public string? Role { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public PortalRoleRecord User { get; set; } = null!;
}

public sealed class PortalNotificationRecord
{
    public int Id { get; set; }
    public int RecipientUserId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ReadAt { get; set; }
}
