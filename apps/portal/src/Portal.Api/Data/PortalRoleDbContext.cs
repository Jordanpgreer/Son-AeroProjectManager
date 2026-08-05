using Microsoft.EntityFrameworkCore;

namespace Portal.Api.Data;

public sealed class PortalRoleDbContext(DbContextOptions<PortalRoleDbContext> options) : DbContext(options)
{
    public DbSet<PortalRoleRecord> Users => Set<PortalRoleRecord>();
    public DbSet<PortalModuleAccessRecord> UserModuleAccess => Set<PortalModuleAccessRecord>();
    public DbSet<PortalNotificationRecord> UserNotifications => Set<PortalNotificationRecord>();
    public DbSet<PortalNotificationProjectRecord> NotificationProjects => Set<PortalNotificationProjectRecord>();
    public DbSet<PortalNotificationTaskRecord> NotificationTasks => Set<PortalNotificationTaskRecord>();
    public DbSet<PortalNotificationMessageRecord> NotificationMessages => Set<PortalNotificationMessageRecord>();
    public DbSet<PortalEngineeringGroupRecord> EngineeringGroups => Set<PortalEngineeringGroupRecord>();
    public DbSet<PortalEngineeringMembershipRecord> EngineeringUserGroupMemberships => Set<PortalEngineeringMembershipRecord>();
    public DbSet<PortalEngineeringPermissionRecord> EngineeringGroupPermissions => Set<PortalEngineeringPermissionRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<PortalRoleRecord>(entity =>
        {
            entity.ToTable("Users");
            entity.HasKey(user => user.Id);
            entity.Property(user => user.AccountName).HasMaxLength(160);
            entity.Property(user => user.DisplayName).HasMaxLength(160);
            entity.Property(user => user.Role).HasMaxLength(32);
            entity.HasMany(user => user.EngineeringGroupMemberships)
                .WithOne(membership => membership.User)
                .HasForeignKey(membership => membership.AppUserId)
                .OnDelete(DeleteBehavior.Cascade);
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

        // Read-only projections over the Project Tracker tables let the Hub count only
        // notifications whose originating project/message/operation still exists.
        modelBuilder.Entity<PortalNotificationProjectRecord>(entity =>
        {
            entity.ToTable("Projects");
            entity.HasKey(project => project.Id);
        });
        modelBuilder.Entity<PortalNotificationTaskRecord>(entity =>
        {
            entity.ToTable("Tasks");
            entity.HasKey(task => task.Id);
        });
        modelBuilder.Entity<PortalNotificationMessageRecord>(entity =>
        {
            entity.ToTable("ProjectMessages");
            entity.HasKey(message => message.Id);
        });

        modelBuilder.Entity<PortalEngineeringGroupRecord>(entity =>
        {
            entity.ToTable("EngineeringGroups");
            entity.HasKey(group => group.Id);
            entity.HasIndex(group => group.Name).IsUnique();
            entity.Property(group => group.Name).HasMaxLength(80);
            entity.Property(group => group.Description).HasMaxLength(240);
            entity.HasMany(group => group.UserMemberships)
                .WithOne(membership => membership.Group)
                .HasForeignKey(membership => membership.AppGroupId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasMany(group => group.Permissions)
                .WithOne(permission => permission.Group)
                .HasForeignKey(permission => permission.AppGroupId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<PortalEngineeringMembershipRecord>(entity =>
        {
            entity.ToTable("EngineeringUserGroupMemberships");
            entity.HasKey(membership => new { membership.AppUserId, membership.AppGroupId });
            entity.HasIndex(membership => membership.AppGroupId);
        });

        modelBuilder.Entity<PortalEngineeringPermissionRecord>(entity =>
        {
            entity.ToTable("EngineeringGroupPermissions");
            entity.HasKey(permission => new { permission.AppGroupId, permission.PermissionKey });
            entity.Property(permission => permission.PermissionKey).HasMaxLength(120);
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
    public ICollection<PortalEngineeringMembershipRecord> EngineeringGroupMemberships { get; set; } = [];
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
    public int ProjectId { get; set; }
    public int? ProjectTaskId { get; set; }
    public int? ProjectMessageId { get; set; }
    public string Kind { get; set; } = string.Empty;
    public string ActorAccountName { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ReadAt { get; set; }
}

public sealed class PortalNotificationProjectRecord
{
    public int Id { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }
}

public sealed class PortalNotificationTaskRecord
{
    public int Id { get; set; }
    public int ProjectId { get; set; }
}

public sealed class PortalNotificationMessageRecord
{
    public int Id { get; set; }
    public int ProjectId { get; set; }
}

public sealed class PortalEngineeringGroupRecord
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public bool IsSystemGroup { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public ICollection<PortalEngineeringMembershipRecord> UserMemberships { get; set; } = [];
    public ICollection<PortalEngineeringPermissionRecord> Permissions { get; set; } = [];
}

public sealed class PortalEngineeringMembershipRecord
{
    public int AppUserId { get; set; }
    public PortalRoleRecord User { get; set; } = null!;
    public int AppGroupId { get; set; }
    public PortalEngineeringGroupRecord Group { get; set; } = null!;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class PortalEngineeringPermissionRecord
{
    public int AppGroupId { get; set; }
    public PortalEngineeringGroupRecord Group { get; set; } = null!;
    public string PermissionKey { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
