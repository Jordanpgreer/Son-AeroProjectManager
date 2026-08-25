using Microsoft.EntityFrameworkCore;
using EstimatingDashboard.Api.Models;
using SonAero.Platform.Security;

namespace EstimatingDashboard.Api.Data;

public sealed class EstimatingAccessDbContext(
    DbContextOptions<EstimatingAccessDbContext> options) : DbContext(options)
{
    public DbSet<EstimatingUserRecord> Users => Set<EstimatingUserRecord>();
    public DbSet<EstimatingModuleAccessRecord> UserModuleAccess =>
        Set<EstimatingModuleAccessRecord>();
    public DbSet<EstimatingAccessGroupRecord> Groups => Set<EstimatingAccessGroupRecord>();
    public DbSet<EstimatingUserGroupMembershipRecord> UserGroupMemberships => Set<EstimatingUserGroupMembershipRecord>();
    public DbSet<EstimatingGroupPermissionRecord> GroupPermissions => Set<EstimatingGroupPermissionRecord>();
    public DbSet<AccessPreviewSessionRecord> AccessPreviewSessions =>
        Set<AccessPreviewSessionRecord>();
    public DbSet<EstimatingQuoteHistoryRecord> QuoteHistory => Set<EstimatingQuoteHistoryRecord>();
    public DbSet<EstimatingQuoteHistoryAuditRecord> QuoteHistoryAudits => Set<EstimatingQuoteHistoryAuditRecord>();
    public DbSet<EstimatingHistoryImportBatch> QuoteHistoryImportBatches => Set<EstimatingHistoryImportBatch>();
    public DbSet<EstimatingEstimatorSettingRecord> EstimatorSettings => Set<EstimatingEstimatorSettingRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<EstimatingUserRecord>(entity =>
        {
            entity.ToTable("Users");
            entity.HasKey(user => user.Id);
            entity.Property(user => user.AccountName).HasMaxLength(160);
            entity.Property(user => user.DisplayName).HasMaxLength(160);
            entity.Property(user => user.PortalRole).HasColumnName("Role").HasMaxLength(32);
            entity.HasMany(user => user.GroupMemberships)
                .WithOne(membership => membership.User)
                .HasForeignKey(membership => membership.AppUserId);
        });

        modelBuilder.Entity<EstimatingAccessGroupRecord>(entity =>
        {
            entity.ToTable("Groups");
            entity.HasKey(group => group.Id);
            entity.Property(group => group.Name).HasMaxLength(80);
            entity.HasMany(group => group.UserMemberships)
                .WithOne(membership => membership.Group)
                .HasForeignKey(membership => membership.AppGroupId);
            entity.HasMany(group => group.Permissions)
                .WithOne(permission => permission.Group)
                .HasForeignKey(permission => permission.AppGroupId);
        });

        modelBuilder.Entity<EstimatingUserGroupMembershipRecord>(entity =>
        {
            entity.ToTable("UserGroupMemberships");
            entity.HasKey(membership => new { membership.AppUserId, membership.AppGroupId });
        });

        modelBuilder.Entity<EstimatingGroupPermissionRecord>(entity =>
        {
            entity.ToTable("GroupPermissions");
            entity.HasKey(permission => new { permission.AppGroupId, permission.PermissionKey });
            entity.Property(permission => permission.PermissionKey).HasMaxLength(120);
        });

        modelBuilder.Entity<EstimatingModuleAccessRecord>(entity =>
        {
            entity.ToTable("UserModuleAccess");
            entity.HasKey(access => new { access.AppUserId, access.ModuleKey });
            entity.Property(access => access.ModuleKey).HasMaxLength(64);
            entity.Property(access => access.Role).HasMaxLength(32);
            entity.HasOne(access => access.User)
                .WithMany(user => user.ModuleAccesses)
                .HasForeignKey(access => access.AppUserId);
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

        modelBuilder.Entity<EstimatingQuoteHistoryRecord>(entity =>
        {
            entity.ToTable("EstimatingQuoteHistory");
            entity.HasKey(record => record.Id);
            entity.HasIndex(record => record.SourceId).IsUnique();
            entity.HasIndex(record => record.QuoteNumber).IsUnique();
            entity.HasIndex(record => record.EstimatingRep);
            entity.HasIndex(record => record.EstimatingCompletionDate);
            entity.HasIndex(record => record.IsCompleted);
            entity.Property(record => record.SourceId).HasMaxLength(80);
            entity.Property(record => record.Customer).HasMaxLength(240);
            entity.Property(record => record.CustomerContact).HasMaxLength(240);
            entity.Property(record => record.SalesPerson).HasMaxLength(160);
            entity.Property(record => record.QuoteStatus).HasMaxLength(80);
            entity.Property(record => record.RfqReferenceNumber).HasMaxLength(500);
            entity.Property(record => record.EstimatingRep).HasMaxLength(160);
            entity.Property(record => record.TotalValue).HasPrecision(18, 2);
            entity.Property(record => record.Issues).HasMaxLength(240);
            entity.Property(record => record.QuoteOnTrack).HasMaxLength(40);
            entity.Property(record => record.QuoteComplexity).HasMaxLength(80);
            entity.Property(record => record.EstimatingStatus).HasMaxLength(160);
            entity.Property(record => record.OnTimeStatus).HasMaxLength(24);
            entity.Property(record => record.OnTimeRatio).HasPrecision(8, 4);
            entity.Property(record => record.CompletedMonth).HasMaxLength(16);
            entity.Property(record => record.CompletedMonthAndWeek).HasMaxLength(40);
            entity.Property(record => record.UpdatedBy).HasMaxLength(160);
        });

        modelBuilder.Entity<EstimatingQuoteHistoryAuditRecord>(entity =>
        {
            entity.ToTable("EstimatingQuoteHistoryAudits");
            entity.HasKey(audit => audit.Id);
            entity.HasIndex(audit => new { audit.QuoteHistoryId, audit.ChangedAt });
            entity.HasIndex(audit => audit.ImportBatchId);
            entity.Property(audit => audit.Action).HasMaxLength(24);
            entity.Property(audit => audit.FieldName).HasMaxLength(120);
            entity.Property(audit => audit.OldValue).HasMaxLength(1000);
            entity.Property(audit => audit.NewValue).HasMaxLength(1000);
            entity.Property(audit => audit.ChangedBy).HasMaxLength(160);
            entity.HasOne(audit => audit.QuoteHistory)
                .WithMany(record => record.AuditHistory)
                .HasForeignKey(audit => audit.QuoteHistoryId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<EstimatingHistoryImportBatch>(entity =>
        {
            entity.ToTable("EstimatingHistoryImportBatches");
            entity.HasKey(batch => batch.Id);
            entity.HasIndex(batch => batch.ImportedAt);
            entity.Property(batch => batch.FileName).HasMaxLength(240);
            entity.Property(batch => batch.FileHash).HasMaxLength(64);
            entity.Property(batch => batch.ImportedBy).HasMaxLength(160);
        });

        modelBuilder.Entity<EstimatingEstimatorSettingRecord>(entity =>
        {
            entity.ToTable("EstimatingEstimatorSettings");
            entity.HasKey(setting => setting.EstimatorKey);
            entity.Property(setting => setting.EstimatorKey).HasMaxLength(160);
            entity.Property(setting => setting.EstimatorName).HasMaxLength(160);
            entity.Property(setting => setting.UpdatedBy).HasMaxLength(160);
        });
    }
}

public sealed class EstimatingUserRecord
{
    public int Id { get; set; }
    public string AccountName { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string PortalRole { get; set; } = string.Empty;
    public bool IsActive { get; set; }
    public ICollection<EstimatingModuleAccessRecord> ModuleAccesses { get; set; } = [];
    public ICollection<EstimatingUserGroupMembershipRecord> GroupMemberships { get; set; } = [];
}

public sealed class EstimatingModuleAccessRecord
{
    public int AppUserId { get; set; }
    public string ModuleKey { get; set; } = string.Empty;
    public string? Role { get; set; }
    public EstimatingUserRecord User { get; set; } = null!;
}

public sealed class EstimatingAccessGroupRecord
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public ICollection<EstimatingUserGroupMembershipRecord> UserMemberships { get; set; } = [];
    public ICollection<EstimatingGroupPermissionRecord> Permissions { get; set; } = [];
}

public sealed class EstimatingUserGroupMembershipRecord
{
    public int AppUserId { get; set; }
    public EstimatingUserRecord User { get; set; } = null!;
    public int AppGroupId { get; set; }
    public EstimatingAccessGroupRecord Group { get; set; } = null!;
}

public sealed class EstimatingGroupPermissionRecord
{
    public int AppGroupId { get; set; }
    public EstimatingAccessGroupRecord Group { get; set; } = null!;
    public string PermissionKey { get; set; } = string.Empty;
}

public sealed class EstimatingEstimatorSettingRecord
{
    public string EstimatorKey { get; set; } = string.Empty;
    public string EstimatorName { get; set; } = string.Empty;
    public bool IsActive { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public string UpdatedBy { get; set; } = string.Empty;
}
