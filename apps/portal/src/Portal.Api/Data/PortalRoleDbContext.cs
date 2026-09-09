using Microsoft.EntityFrameworkCore;
using SonAero.Platform.Security;

namespace Portal.Api.Data;

public sealed class PortalRoleDbContext(DbContextOptions<PortalRoleDbContext> options) : DbContext(options)
{
    public DbSet<PortalRoleRecord> Users => Set<PortalRoleRecord>();
    public DbSet<PortalModuleAccessRecord> UserModuleAccess => Set<PortalModuleAccessRecord>();
    public DbSet<PortalNotificationRecord> UserNotifications => Set<PortalNotificationRecord>();
    public DbSet<PortalNotificationProjectRecord> NotificationProjects => Set<PortalNotificationProjectRecord>();
    public DbSet<PortalNotificationTaskRecord> NotificationTasks => Set<PortalNotificationTaskRecord>();
    public DbSet<PortalNotificationMessageRecord> NotificationMessages => Set<PortalNotificationMessageRecord>();
    public DbSet<ArdaPushSubscriptionRecord> ArdaPushSubscriptions => Set<ArdaPushSubscriptionRecord>();
    public DbSet<ArdaPushNotificationRecord> ArdaPushNotifications => Set<ArdaPushNotificationRecord>();
    public DbSet<PortalEngineeringGroupRecord> EngineeringGroups => Set<PortalEngineeringGroupRecord>();
    public DbSet<PortalProjectTrackerGroupRecord> ProjectTrackerGroups => Set<PortalProjectTrackerGroupRecord>();
    public DbSet<PortalEngineeringMembershipRecord> EngineeringUserGroupMemberships => Set<PortalEngineeringMembershipRecord>();
    public DbSet<PortalEngineeringPermissionRecord> EngineeringGroupPermissions => Set<PortalEngineeringPermissionRecord>();
    public DbSet<PortalProjectTrackerMembershipRecord> ProjectTrackerUserGroupMemberships => Set<PortalProjectTrackerMembershipRecord>();
    public DbSet<PortalProjectTrackerPermissionRecord> ProjectTrackerGroupPermissions => Set<PortalProjectTrackerPermissionRecord>();
    public DbSet<AccessPreviewSessionRecord> AccessPreviewSessions => Set<AccessPreviewSessionRecord>();
    public DbSet<PortalEngineeringStorageSettingRecord> EngineeringStorageSettings => Set<PortalEngineeringStorageSettingRecord>();
    public DbSet<PortalEstimatingQuoteHistoryRecord> EstimatingQuoteHistory => Set<PortalEstimatingQuoteHistoryRecord>();
    public DbSet<PortalEstimatorSettingRecord> EstimatorSettings => Set<PortalEstimatorSettingRecord>();
    public DbSet<PortalIntegrationCredentialRecord> IntegrationCredentials => Set<PortalIntegrationCredentialRecord>();
    public DbSet<PortalIntegrationCredentialTestRecord> IntegrationCredentialTests => Set<PortalIntegrationCredentialTestRecord>();
    public DbSet<PortalEnterpriseIntegrationSettingRecord> EnterpriseIntegrationSettings => Set<PortalEnterpriseIntegrationSettingRecord>();
    public DbSet<PortalEnterpriseIntegrationSettingAuditRecord> EnterpriseIntegrationSettingAudits => Set<PortalEnterpriseIntegrationSettingAuditRecord>();
    public DbSet<RaidLogGroupRecord> RaidLogGroups => Set<RaidLogGroupRecord>();
    public DbSet<RaidLogItemRecord> RaidLogItems => Set<RaidLogItemRecord>();
    public DbSet<RaidLogNoteRecord> RaidLogNotes => Set<RaidLogNoteRecord>();
    public DbSet<RaidLogActivityRecord> RaidLogActivity => Set<RaidLogActivityRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Portal.Api.Services.ApiCustomizer.CustomizerReportRecord>(entity =>
        {
            entity.ToTable("ApiCustomizerReports");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Name).HasMaxLength(120);
            entity.Property(x => x.UpdatedBy).HasMaxLength(160);
            entity.Property(x => x.Version).IsConcurrencyToken();
        });
        modelBuilder.Entity<Portal.Api.Services.ApiCustomizer.CustomizerAuditRecord>(entity =>
        {
            entity.ToTable("ApiCustomizerAudits");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Actor).HasMaxLength(160);
            entity.Property(x => x.Action).HasMaxLength(32);
            entity.Property(x => x.ReportName).HasMaxLength(120);
        });
        modelBuilder.Entity<PortalRoleRecord>(entity =>
        {
            entity.ToTable("Users");
            entity.HasKey(user => user.Id);
            entity.Property(user => user.AccountName).HasMaxLength(160);
            entity.Property(user => user.DisplayName).HasMaxLength(160);
            entity.Property(user => user.Role).HasMaxLength(32);
            entity.HasIndex(user => user.AccountName).IsUnique();
            entity.HasMany(user => user.EngineeringGroupMemberships)
                .WithOne(membership => membership.User)
                .HasForeignKey(membership => membership.AppUserId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasMany(user => user.ProjectTrackerGroupMemberships)
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

        modelBuilder.Entity<PortalProjectTrackerGroupRecord>(entity =>
        {
            entity.ToTable("Groups");
            entity.HasKey(group => group.Id);
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

        modelBuilder.Entity<PortalProjectTrackerMembershipRecord>(entity =>
        {
            entity.ToTable("UserGroupMemberships");
            entity.HasKey(membership => new { membership.AppUserId, membership.AppGroupId });
            entity.HasIndex(membership => membership.AppGroupId);
        });

        modelBuilder.Entity<PortalProjectTrackerPermissionRecord>(entity =>
        {
            entity.ToTable("GroupPermissions");
            entity.HasKey(permission => new { permission.AppGroupId, permission.PermissionKey });
            entity.Property(permission => permission.PermissionKey).HasMaxLength(120);
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

        modelBuilder.Entity<PortalEngineeringStorageSettingRecord>(entity =>
        {
            entity.ToTable("EngineeringStorageSettings");
            entity.HasKey(setting => setting.Id);
            entity.Property(setting => setting.RootPath).HasMaxLength(2048);
            entity.Property(setting => setting.UpdatedBy).HasMaxLength(160);
        });

        modelBuilder.Entity<PortalEstimatingQuoteHistoryRecord>(entity =>
        {
            entity.ToTable("EstimatingQuoteHistory");
            entity.HasKey(record => record.Id);
            entity.Property(record => record.EstimatingRep).HasMaxLength(160);
        });

        modelBuilder.Entity<PortalEstimatorSettingRecord>(entity =>
        {
            entity.ToTable("EstimatingEstimatorSettings");
            entity.HasKey(setting => setting.EstimatorKey);
            entity.Property(setting => setting.EstimatorKey).HasMaxLength(160);
            entity.Property(setting => setting.EstimatorName).HasMaxLength(160);
            entity.Property(setting => setting.UpdatedBy).HasMaxLength(160);
        });

        modelBuilder.Entity<PortalIntegrationCredentialRecord>(entity =>
        {
            entity.ToTable("IntegrationCredentials");
            entity.HasKey(credential => credential.CredentialKey);
            entity.Property(credential => credential.CredentialKey).HasMaxLength(120);
            entity.Property(credential => credential.DisplayName).HasMaxLength(160);
            entity.Property(credential => credential.UpdatedBy).HasMaxLength(160);
        });

        modelBuilder.Entity<PortalIntegrationCredentialTestRecord>(entity =>
        {
            entity.ToTable("IntegrationCredentialTests");
            entity.HasKey(test => test.CredentialKey);
            entity.Property(test => test.CredentialKey).HasMaxLength(120);
            entity.Property(test => test.Message).HasMaxLength(500);
            entity.Property(test => test.TestedBy).HasMaxLength(160);
        });

        modelBuilder.Entity<PortalEnterpriseIntegrationSettingRecord>(entity =>
        {
            entity.ToTable("EnterpriseIntegrationSettings");
            entity.HasKey(setting => setting.Id);
            entity.Property(setting => setting.ActiveProvider).HasMaxLength(40);
            entity.Property(setting => setting.UpdatedBy).HasMaxLength(160);
        });

        modelBuilder.Entity<PortalEnterpriseIntegrationSettingAuditRecord>(entity =>
        {
            entity.ToTable("EnterpriseIntegrationSettingAudits");
            entity.HasKey(audit => audit.Id);
            entity.HasIndex(audit => audit.ChangedAt);
            entity.Property(audit => audit.PreviousProvider).HasMaxLength(40);
            entity.Property(audit => audit.NewProvider).HasMaxLength(40);
            entity.Property(audit => audit.ChangedBy).HasMaxLength(160);
        });

        modelBuilder.Entity<ArdaPushSubscriptionRecord>(entity =>
        {
            entity.ToTable("ArdaPushSubscriptions");
            entity.HasKey(subscription => subscription.Id);
            entity.HasIndex(subscription => subscription.Endpoint).IsUnique();
            entity.HasIndex(subscription => subscription.AppUserId);
            entity.Property(subscription => subscription.Endpoint).HasMaxLength(2048);
            entity.Property(subscription => subscription.P256dh).HasMaxLength(256);
            entity.Property(subscription => subscription.Auth).HasMaxLength(128);
            entity.HasOne(subscription => subscription.User)
                .WithMany()
                .HasForeignKey(subscription => subscription.AppUserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ArdaPushNotificationRecord>(entity =>
        {
            entity.ToTable("ArdaPushNotifications");
            entity.HasKey(notification => notification.Id);
            entity.HasIndex(notification => new { notification.SourceModule, notification.SourceNotificationKey })
                .IsUnique();
            entity.HasIndex(notification => new { notification.DeliveredAt, notification.NextAttemptAt });
            entity.Property(notification => notification.RecipientAccountName).HasMaxLength(160);
            entity.Property(notification => notification.SourceModule).HasMaxLength(64);
            entity.Property(notification => notification.SourceNotificationKey).HasMaxLength(160);
            entity.Property(notification => notification.Title).HasMaxLength(160);
            entity.Property(notification => notification.Body).HasMaxLength(500);
            entity.Property(notification => notification.TargetUrl).HasMaxLength(2048);
            entity.Property(notification => notification.DeliveryMethod).HasMaxLength(24);
            entity.Property(notification => notification.LastError).HasMaxLength(1000);
            entity.Property(notification => notification.ForegroundClaimedBy).HasMaxLength(256);
        });

        modelBuilder.Entity<RaidLogGroupRecord>(entity =>
        {
            entity.ToTable("RaidLogGroups");
            entity.HasKey(group => group.Id);
            entity.Property(group => group.Name).HasMaxLength(120);
            entity.Property(group => group.NormalizedName).HasMaxLength(120);
            entity.HasIndex(group => group.NormalizedName).IsUnique();
            entity.Property(group => group.Description).HasMaxLength(500);
            entity.Property(group => group.CreatedBy).HasMaxLength(160);
            entity.Property(group => group.UpdatedBy).HasMaxLength(160);
            entity.Property(group => group.Version).IsConcurrencyToken();
            entity.HasMany(group => group.Items)
                .WithOne(item => item.Group)
                .HasForeignKey(item => item.GroupId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<RaidLogItemRecord>(entity =>
        {
            entity.ToTable("RaidLogItems");
            entity.HasKey(item => item.Id);
            entity.HasIndex(item => new { item.GroupId, item.CompletedAt, item.Priority });
            entity.Property(item => item.Title).HasMaxLength(240);
            entity.Property(item => item.Description).HasMaxLength(4000);
            entity.Property(item => item.Kind).HasMaxLength(24);
            entity.Property(item => item.Priority).HasMaxLength(24);
            entity.Property(item => item.CreatedBy).HasMaxLength(160);
            entity.Property(item => item.UpdatedBy).HasMaxLength(160);
            entity.Property(item => item.CompletedBy).HasMaxLength(160);
            entity.Property(item => item.Version).IsConcurrencyToken();
            entity.HasOne(item => item.AssignedToUser)
                .WithMany()
                .HasForeignKey(item => item.AssignedToUserId)
                .OnDelete(DeleteBehavior.SetNull);
            entity.HasMany(item => item.Notes)
                .WithOne(note => note.Item)
                .HasForeignKey(note => note.ItemId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasMany(item => item.Activity)
                .WithOne(activity => activity.Item)
                .HasForeignKey(activity => activity.ItemId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<RaidLogNoteRecord>(entity =>
        {
            entity.ToTable("RaidLogNotes");
            entity.HasKey(note => note.Id);
            entity.HasIndex(note => new { note.ItemId, note.CreatedAt });
            entity.Property(note => note.Body).HasMaxLength(4000);
            entity.Property(note => note.CreatedBy).HasMaxLength(160);
        });

        modelBuilder.Entity<RaidLogActivityRecord>(entity =>
        {
            entity.ToTable("RaidLogActivity");
            entity.HasKey(activity => activity.Id);
            entity.HasIndex(activity => new { activity.ItemId, activity.OccurredAt });
            entity.Property(activity => activity.Action).HasMaxLength(32);
            entity.Property(activity => activity.Summary).HasMaxLength(500);
            entity.Property(activity => activity.Actor).HasMaxLength(160);
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
    public ICollection<PortalProjectTrackerMembershipRecord> ProjectTrackerGroupMemberships { get; set; } = [];
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

/// <summary>Read-only projection used by the Hub's administrator access preview.</summary>
public sealed class PortalProjectTrackerGroupRecord
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public bool IsSystemGroup { get; set; }
    public ICollection<PortalProjectTrackerMembershipRecord> UserMemberships { get; set; } = [];
    public ICollection<PortalProjectTrackerPermissionRecord> Permissions { get; set; } = [];
}

public sealed class PortalProjectTrackerMembershipRecord
{
    public int AppUserId { get; set; }
    public PortalRoleRecord User { get; set; } = null!;
    public int AppGroupId { get; set; }
    public PortalProjectTrackerGroupRecord Group { get; set; } = null!;
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class PortalProjectTrackerPermissionRecord
{
    public int AppGroupId { get; set; }
    public PortalProjectTrackerGroupRecord Group { get; set; } = null!;
    public string PermissionKey { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
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

public sealed class PortalEngineeringStorageSettingRecord
{
    public int Id { get; set; }
    public string RootPath { get; set; } = string.Empty;
    public string PreviousRootPathsJson { get; set; } = "[]";
    public DateTimeOffset UpdatedAt { get; set; }
    public string UpdatedBy { get; set; } = string.Empty;
}

public sealed class PortalEstimatingQuoteHistoryRecord
{
    public int Id { get; set; }
    public string EstimatingRep { get; set; } = string.Empty;
}

public sealed class PortalEstimatorSettingRecord
{
    public string EstimatorKey { get; set; } = string.Empty;
    public string EstimatorName { get; set; } = string.Empty;
    public bool IsActive { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public string UpdatedBy { get; set; } = string.Empty;
}

public sealed class PortalIntegrationCredentialRecord
{
    public string CredentialKey { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string EncryptedSecret { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public string UpdatedBy { get; set; } = string.Empty;
    public DateTimeOffset? ExpiresAt { get; set; }
    public DateTimeOffset? LastUsedAt { get; set; }
}

public sealed class PortalIntegrationCredentialTestRecord
{
    public string CredentialKey { get; set; } = string.Empty;
    public DateTimeOffset TestedAt { get; set; }
    public bool Succeeded { get; set; }
    public string Message { get; set; } = string.Empty;
    public int? HttpStatusCode { get; set; }
    public string TestedBy { get; set; } = string.Empty;
}

public sealed class PortalEnterpriseIntegrationSettingRecord
{
    public int Id { get; set; }
    public string ActiveProvider { get; set; } = string.Empty;
    public DateTimeOffset UpdatedAt { get; set; }
    public string UpdatedBy { get; set; } = string.Empty;
}

public sealed class PortalEnterpriseIntegrationSettingAuditRecord
{
    public long Id { get; set; }
    public string PreviousProvider { get; set; } = string.Empty;
    public string NewProvider { get; set; } = string.Empty;
    public DateTimeOffset ChangedAt { get; set; }
    public string ChangedBy { get; set; } = string.Empty;
}

public sealed class ArdaPushSubscriptionRecord
{
    public long Id { get; set; }
    public int AppUserId { get; set; }
    public string Endpoint { get; set; } = string.Empty;
    public string P256dh { get; set; } = string.Empty;
    public string Auth { get; set; } = string.Empty;
    public DateTimeOffset? ExpirationTime { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public PortalRoleRecord User { get; set; } = null!;
}

public sealed class ArdaPushNotificationRecord
{
    public long Id { get; set; }
    public string RecipientAccountName { get; set; } = string.Empty;
    public string SourceModule { get; set; } = string.Empty;
    public string SourceNotificationKey { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;
    public string TargetUrl { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset NextAttemptAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? DeliveredAt { get; set; }
    public string? DeliveryMethod { get; set; }
    public int AttemptCount { get; set; }
    public string? LastError { get; set; }
    public string? ForegroundClaimedBy { get; set; }
    public DateTimeOffset? ForegroundClaimExpiresAt { get; set; }
}

public sealed class RaidLogGroupRecord
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string NormalizedName { get; set; } = string.Empty;
    public string? Description { get; set; }
    public int SortOrder { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public string CreatedBy { get; set; } = string.Empty;
    public DateTimeOffset UpdatedAt { get; set; }
    public string UpdatedBy { get; set; } = string.Empty;
    public long Version { get; set; }
    public ICollection<RaidLogItemRecord> Items { get; set; } = [];
}

public sealed class RaidLogItemRecord
{
    public int Id { get; set; }
    public int GroupId { get; set; }
    public RaidLogGroupRecord Group { get; set; } = null!;
    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string Kind { get; set; } = "Action";
    public string Priority { get; set; } = "Normal";
    public int? AssignedToUserId { get; set; }
    public PortalRoleRecord? AssignedToUser { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public string CreatedBy { get; set; } = string.Empty;
    public DateTimeOffset UpdatedAt { get; set; }
    public string UpdatedBy { get; set; } = string.Empty;
    public DateTimeOffset? CompletedAt { get; set; }
    public string? CompletedBy { get; set; }
    public long Version { get; set; }
    public ICollection<RaidLogNoteRecord> Notes { get; set; } = [];
    public ICollection<RaidLogActivityRecord> Activity { get; set; } = [];
}

public sealed class RaidLogNoteRecord
{
    public long Id { get; set; }
    public int ItemId { get; set; }
    public RaidLogItemRecord Item { get; set; } = null!;
    public string Body { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public string CreatedBy { get; set; } = string.Empty;
}

public sealed class RaidLogActivityRecord
{
    public long Id { get; set; }
    public int ItemId { get; set; }
    public RaidLogItemRecord Item { get; set; } = null!;
    public string Action { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;
    public DateTimeOffset OccurredAt { get; set; }
    public string Actor { get; set; } = string.Empty;
}
