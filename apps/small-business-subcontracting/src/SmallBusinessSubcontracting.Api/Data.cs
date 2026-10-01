using Microsoft.EntityFrameworkCore;

namespace SmallBusinessSubcontracting.Api;

public sealed class SubcontractingDbContext(DbContextOptions<SubcontractingDbContext> options) : DbContext(options)
{
    public DbSet<VendorRecord> Vendors => Set<VendorRecord>();
    public DbSet<BusinessSizeTag> BusinessSizeTags => Set<BusinessSizeTag>();
    public DbSet<VendorBusinessSizeTag> VendorBusinessSizeTags => Set<VendorBusinessSizeTag>();
    public DbSet<VendorDocument> VendorDocuments => Set<VendorDocument>();
    public DbSet<VendorAuditEvent> VendorAuditEvents => Set<VendorAuditEvent>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<VendorRecord>(entity =>
        {
            entity.HasKey(vendor => vendor.Id);
            entity.HasIndex(vendor => vendor.FulcrumId).IsUnique();
            entity.HasIndex(vendor => vendor.Name);
            entity.Property(vendor => vendor.FulcrumId).HasMaxLength(80);
            entity.Property(vendor => vendor.Name).HasMaxLength(200);
            entity.Property(vendor => vendor.VendorCode).HasMaxLength(120);
            entity.Property(vendor => vendor.Website).HasMaxLength(500);
        });
        modelBuilder.Entity<BusinessSizeTag>(entity =>
        {
            entity.HasKey(tag => tag.Id);
            entity.Property(tag => tag.Name).HasMaxLength(80);
            entity.Property(tag => tag.NormalizedName).HasMaxLength(80);
            entity.HasIndex(tag => tag.NormalizedName).IsUnique();
        });
        modelBuilder.Entity<VendorBusinessSizeTag>(entity =>
        {
            entity.HasKey(link => new { link.VendorId, link.BusinessSizeTagId });
            entity.HasOne(link => link.Vendor).WithMany(vendor => vendor.BusinessSizes)
                .HasForeignKey(link => link.VendorId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(link => link.BusinessSizeTag).WithMany(tag => tag.Vendors)
                .HasForeignKey(link => link.BusinessSizeTagId).OnDelete(DeleteBehavior.Cascade);
        });
        modelBuilder.Entity<VendorDocument>(entity =>
        {
            entity.HasKey(document => document.Id);
            entity.Property(document => document.OriginalFileName).HasMaxLength(255);
            entity.Property(document => document.RelativePath).HasMaxLength(1000);
            entity.Property(document => document.ContentType).HasMaxLength(160);
            entity.Property(document => document.FileHash).HasMaxLength(64);
            entity.Property(document => document.DocumentType).HasMaxLength(100);
            entity.Property(document => document.Notes).HasMaxLength(1000);
            entity.Property(document => document.UploadedBy).HasMaxLength(160);
            entity.HasOne(document => document.Vendor).WithMany(vendor => vendor.Documents)
                .HasForeignKey(document => document.VendorId).OnDelete(DeleteBehavior.Cascade);
        });
        modelBuilder.Entity<VendorAuditEvent>(entity =>
        {
            entity.HasKey(audit => audit.Id);
            entity.Property(audit => audit.Kind).HasMaxLength(80);
            entity.Property(audit => audit.Summary).HasMaxLength(500);
            entity.Property(audit => audit.Actor).HasMaxLength(160);
            entity.HasOne(audit => audit.Vendor).WithMany(vendor => vendor.AuditEvents)
                .HasForeignKey(audit => audit.VendorId).OnDelete(DeleteBehavior.Cascade);
        });
    }
}

public sealed class RoleStoreDbContext(DbContextOptions<RoleStoreDbContext> options) : DbContext(options)
{
    public DbSet<RoleUser> Users => Set<RoleUser>();
    public DbSet<RoleAccessGroup> Groups => Set<RoleAccessGroup>();
    public DbSet<RoleUserGroupMembership> UserGroupMemberships => Set<RoleUserGroupMembership>();
    public DbSet<RoleGroupPermission> GroupPermissions => Set<RoleGroupPermission>();
    public DbSet<RoleIntegrationCredential> IntegrationCredentials => Set<RoleIntegrationCredential>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<RoleUser>(entity =>
        {
            entity.ToTable("Users");
            entity.HasKey(user => user.Id);
            entity.Property(user => user.AccountName).HasMaxLength(160);
            entity.HasMany(user => user.ModuleAssignments).WithOne(assignment => assignment.User)
                .HasForeignKey(assignment => assignment.AppUserId);
            entity.HasMany(user => user.GroupMemberships).WithOne(membership => membership.User)
                .HasForeignKey(membership => membership.AppUserId);
        });
        modelBuilder.Entity<RoleModuleAssignment>(entity =>
        {
            entity.ToTable("UserModuleAccess");
            entity.HasKey(assignment => new { assignment.AppUserId, assignment.ModuleKey });
            entity.Property(assignment => assignment.ModuleKey).HasMaxLength(40);
            entity.Property(assignment => assignment.Role).HasMaxLength(32);
        });
        modelBuilder.Entity<RoleAccessGroup>(entity =>
        {
            entity.ToTable("Groups");
            entity.HasKey(group => group.Id);
            entity.Property(group => group.Name).HasMaxLength(80);
            entity.HasMany(group => group.UserMemberships).WithOne(membership => membership.Group)
                .HasForeignKey(membership => membership.AppGroupId);
            entity.HasMany(group => group.Permissions).WithOne(permission => permission.Group)
                .HasForeignKey(permission => permission.AppGroupId);
        });
        modelBuilder.Entity<RoleUserGroupMembership>(entity =>
        {
            entity.ToTable("UserGroupMemberships");
            entity.HasKey(membership => new { membership.AppUserId, membership.AppGroupId });
        });
        modelBuilder.Entity<RoleGroupPermission>(entity =>
        {
            entity.ToTable("GroupPermissions");
            entity.HasKey(permission => new { permission.AppGroupId, permission.PermissionKey });
            entity.Property(permission => permission.PermissionKey).HasMaxLength(120);
        });
        modelBuilder.Entity<RoleIntegrationCredential>(entity =>
        {
            entity.ToTable("IntegrationCredentials");
            entity.HasKey(credential => credential.CredentialKey);
            entity.Property(credential => credential.CredentialKey).HasMaxLength(120);
        });
    }
}

public sealed class RoleUser
{
    public int Id { get; set; }
    public string AccountName { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public bool IsActive { get; set; }
    public ICollection<RoleModuleAssignment> ModuleAssignments { get; set; } = [];
    public ICollection<RoleUserGroupMembership> GroupMemberships { get; set; } = [];
}

public sealed class RoleModuleAssignment
{
    public int AppUserId { get; set; }
    public RoleUser User { get; set; } = null!;
    public string ModuleKey { get; set; } = string.Empty;
    public string? Role { get; set; }
}

public sealed class RoleIntegrationCredential
{
    public string CredentialKey { get; set; } = string.Empty;
    public string EncryptedSecret { get; set; } = string.Empty;
    public DateTimeOffset? ExpiresAt { get; set; }
}

public sealed class RoleAccessGroup
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public ICollection<RoleUserGroupMembership> UserMemberships { get; set; } = [];
    public ICollection<RoleGroupPermission> Permissions { get; set; } = [];
}

public sealed class RoleUserGroupMembership
{
    public int AppUserId { get; set; }
    public RoleUser User { get; set; } = null!;
    public int AppGroupId { get; set; }
    public RoleAccessGroup Group { get; set; } = null!;
}

public sealed class RoleGroupPermission
{
    public int AppGroupId { get; set; }
    public RoleAccessGroup Group { get; set; } = null!;
    public string PermissionKey { get; set; } = string.Empty;
}
