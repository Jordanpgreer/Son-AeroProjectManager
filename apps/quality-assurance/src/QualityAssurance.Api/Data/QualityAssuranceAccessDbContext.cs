using Microsoft.EntityFrameworkCore;

namespace QualityAssurance.Api.Data;

public sealed class QualityAssuranceAccessDbContext(
    DbContextOptions<QualityAssuranceAccessDbContext> options) : DbContext(options)
{
    public DbSet<QualityAssuranceUserRecord> Users => Set<QualityAssuranceUserRecord>();
    public DbSet<QualityAssuranceModuleAccessRecord> UserModuleAccess =>
        Set<QualityAssuranceModuleAccessRecord>();
    public DbSet<QualityAssuranceAccessGroupRecord> Groups => Set<QualityAssuranceAccessGroupRecord>();
    public DbSet<QualityAssuranceUserGroupMembershipRecord> UserGroupMemberships => Set<QualityAssuranceUserGroupMembershipRecord>();
    public DbSet<QualityAssuranceGroupPermissionRecord> GroupPermissions => Set<QualityAssuranceGroupPermissionRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<QualityAssuranceUserRecord>(entity =>
        {
            entity.ToTable("Users");
            entity.HasKey(user => user.Id);
            entity.Property(user => user.AccountName).HasMaxLength(160);
            entity.Property(user => user.DisplayName).HasMaxLength(160);
            entity.Property(user => user.PortalRole).HasColumnName("Role").HasMaxLength(32);
            entity.Property(user => user.LastSeenAt);
            entity.HasMany(user => user.GroupMemberships)
                .WithOne(membership => membership.User)
                .HasForeignKey(membership => membership.AppUserId);
        });

        modelBuilder.Entity<QualityAssuranceAccessGroupRecord>(entity =>
        {
            entity.ToTable("Groups");
            entity.HasKey(group => group.Id);
            entity.Property(group => group.Name).HasMaxLength(120);
            entity.Property(group => group.Description).HasMaxLength(500);
            entity.HasMany(group => group.UserMemberships)
                .WithOne(membership => membership.Group)
                .HasForeignKey(membership => membership.AppGroupId);
            entity.HasMany(group => group.Permissions)
                .WithOne(permission => permission.Group)
                .HasForeignKey(permission => permission.AppGroupId);
        });

        modelBuilder.Entity<QualityAssuranceUserGroupMembershipRecord>(entity =>
        {
            entity.ToTable("UserGroupMemberships");
            entity.HasKey(membership => new { membership.AppUserId, membership.AppGroupId });
        });

        modelBuilder.Entity<QualityAssuranceGroupPermissionRecord>(entity =>
        {
            entity.ToTable("GroupPermissions");
            entity.HasKey(permission => new { permission.AppGroupId, permission.PermissionKey });
            entity.Property(permission => permission.PermissionKey).HasMaxLength(120);
        });

        modelBuilder.Entity<QualityAssuranceModuleAccessRecord>(entity =>
        {
            entity.ToTable("UserModuleAccess");
            entity.HasKey(access => new { access.AppUserId, access.ModuleKey });
            entity.Property(access => access.ModuleKey).HasMaxLength(64);
            entity.Property(access => access.Role).HasMaxLength(32);
            entity.HasOne(access => access.User)
                .WithMany(user => user.ModuleAccesses)
                .HasForeignKey(access => access.AppUserId);
        });
    }
}

public sealed class QualityAssuranceUserRecord
{
    public int Id { get; set; }
    public string AccountName { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string PortalRole { get; set; } = string.Empty;
    public bool IsActive { get; set; }
    public DateTimeOffset LastSeenAt { get; set; }
    public ICollection<QualityAssuranceModuleAccessRecord> ModuleAccesses { get; set; } = [];
    public ICollection<QualityAssuranceUserGroupMembershipRecord> GroupMemberships { get; set; } = [];
}

public sealed class QualityAssuranceModuleAccessRecord
{
    public int AppUserId { get; set; }
    public string ModuleKey { get; set; } = string.Empty;
    public string? Role { get; set; }
    public QualityAssuranceUserRecord User { get; set; } = null!;
}

public sealed class QualityAssuranceAccessGroupRecord
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public ICollection<QualityAssuranceUserGroupMembershipRecord> UserMemberships { get; set; } = [];
    public ICollection<QualityAssuranceGroupPermissionRecord> Permissions { get; set; } = [];
}

public sealed class QualityAssuranceUserGroupMembershipRecord
{
    public int AppUserId { get; set; }
    public QualityAssuranceUserRecord User { get; set; } = null!;
    public int AppGroupId { get; set; }
    public QualityAssuranceAccessGroupRecord Group { get; set; } = null!;
}

public sealed class QualityAssuranceGroupPermissionRecord
{
    public int AppGroupId { get; set; }
    public QualityAssuranceAccessGroupRecord Group { get; set; } = null!;
    public string PermissionKey { get; set; } = string.Empty;
}
