using Microsoft.EntityFrameworkCore;
using SonAero.Platform.Security;

namespace EngineeringHub.Api.Data;

public sealed class EngineeringRoleDbContext(DbContextOptions<EngineeringRoleDbContext> options) : DbContext(options)
{
    public DbSet<EngineeringUserRecord> Users => Set<EngineeringUserRecord>();
    public DbSet<EngineeringModuleAccessRecord> UserModuleAccess => Set<EngineeringModuleAccessRecord>();
    public DbSet<EngineeringAccessGroupRecord> Groups => Set<EngineeringAccessGroupRecord>();
    public DbSet<EngineeringUserGroupMembershipRecord> UserGroupMemberships => Set<EngineeringUserGroupMembershipRecord>();
    public DbSet<EngineeringGroupPermissionRecord> GroupPermissions => Set<EngineeringGroupPermissionRecord>();
    public DbSet<AccessPreviewSessionRecord> AccessPreviewSessions => Set<AccessPreviewSessionRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<EngineeringUserRecord>(entity =>
        {
            entity.ToTable("Users");
            entity.HasKey(user => user.Id);
            entity.Property(user => user.AccountName).HasMaxLength(160);
            entity.Property(user => user.DisplayName).HasMaxLength(160);
            entity.Property<string>("Role").HasMaxLength(32).HasDefaultValue("Viewer").IsRequired();
            entity.HasIndex(user => user.AccountName).IsUnique();
            entity.HasMany(user => user.GroupMemberships)
                .WithOne(membership => membership.User)
                .HasForeignKey(membership => membership.AppUserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<EngineeringModuleAccessRecord>(entity =>
        {
            entity.ToTable("UserModuleAccess");
            entity.HasKey(access => new { access.AppUserId, access.ModuleKey });
            entity.Property(access => access.ModuleKey).HasMaxLength(40);
            entity.Property(access => access.Role).HasMaxLength(32);
            entity.HasOne(access => access.User)
                .WithMany(user => user.ModuleAccessAssignments)
                .HasForeignKey(access => access.AppUserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<EngineeringAccessGroupRecord>(entity =>
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

        modelBuilder.Entity<EngineeringUserGroupMembershipRecord>(entity =>
        {
            entity.ToTable("EngineeringUserGroupMemberships");
            entity.HasKey(membership => new { membership.AppUserId, membership.AppGroupId });
            entity.HasIndex(membership => membership.AppGroupId);
        });

        modelBuilder.Entity<EngineeringGroupPermissionRecord>(entity =>
        {
            entity.ToTable("EngineeringGroupPermissions");
            entity.HasKey(permission => new { permission.AppGroupId, permission.PermissionKey });
            entity.Property(permission => permission.PermissionKey).HasMaxLength(120);
        });

        modelBuilder.Entity<AccessPreviewSessionRecord>(entity =>
        {
            entity.ToTable("AccessPreviewSessions");
            entity.HasKey(session => session.Id);
            entity.HasIndex(session => session.TokenHash).IsUnique();
            entity.Property(session => session.TokenHash).HasMaxLength(64);
            entity.Property(session => session.AdministratorAccountName).HasMaxLength(160);
            entity.Property(session => session.TargetKey).HasMaxLength(96);
            entity.Property(session => session.ApplicationId).HasMaxLength(64);
        });
    }
}

public sealed class EngineeringUserRecord
{
    public int Id { get; set; }
    public string AccountName { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public bool IsActive { get; set; }
    public DateTimeOffset LastSeenAt { get; set; }
    public ICollection<EngineeringModuleAccessRecord> ModuleAccessAssignments { get; set; } = [];
    public ICollection<EngineeringUserGroupMembershipRecord> GroupMemberships { get; set; } = [];
}

public sealed class EngineeringModuleAccessRecord
{
    public int AppUserId { get; set; }
    public EngineeringUserRecord User { get; set; } = null!;
    public string ModuleKey { get; set; } = string.Empty;
    public string? Role { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class EngineeringAccessGroupRecord
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public bool IsSystemGroup { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public ICollection<EngineeringUserGroupMembershipRecord> UserMemberships { get; set; } = [];
    public ICollection<EngineeringGroupPermissionRecord> Permissions { get; set; } = [];
}

public sealed class EngineeringUserGroupMembershipRecord
{
    public int AppUserId { get; set; }
    public EngineeringUserRecord User { get; set; } = null!;
    public int AppGroupId { get; set; }
    public EngineeringAccessGroupRecord Group { get; set; } = null!;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class EngineeringGroupPermissionRecord
{
    public int AppGroupId { get; set; }
    public EngineeringAccessGroupRecord Group { get; set; } = null!;
    public string PermissionKey { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
