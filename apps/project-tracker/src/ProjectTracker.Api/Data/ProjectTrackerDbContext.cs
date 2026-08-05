using Microsoft.EntityFrameworkCore;
using ProjectTracker.Api.Models;
using SonAero.Platform.Security;

namespace ProjectTracker.Api.Data;

public sealed class ProjectTrackerDbContext(DbContextOptions<ProjectTrackerDbContext> options) : DbContext(options)
{
    public DbSet<Project> Projects => Set<Project>();
    public DbSet<ProjectMessage> ProjectMessages => Set<ProjectMessage>();
    public DbSet<ProjectAuditEntry> ProjectAuditEntries => Set<ProjectAuditEntry>();
    public DbSet<UserNotification> UserNotifications => Set<UserNotification>();
    public DbSet<ProjectTask> Tasks => Set<ProjectTask>();
    public DbSet<Phase> Phases => Set<Phase>();
    public DbSet<Holiday> Holidays => Set<Holiday>();
    public DbSet<WorkCenter> WorkCenters => Set<WorkCenter>();
    public DbSet<ScheduleSettings> ScheduleSettings => Set<ScheduleSettings>();
    public DbSet<TaskOvertimeDay> TaskOvertimeDays => Set<TaskOvertimeDay>();
    public DbSet<AppUser> Users => Set<AppUser>();
    public DbSet<AppGroup> Groups => Set<AppGroup>();
    public DbSet<AppUserGroupMembership> UserGroupMemberships => Set<AppUserGroupMembership>();
    public DbSet<AppGroupPermission> GroupPermissions => Set<AppGroupPermission>();
    public DbSet<AppUserModuleAccess> UserModuleAccess => Set<AppUserModuleAccess>();
    public DbSet<StatusHistory> StatusHistory => Set<StatusHistory>();
    public DbSet<AccessPreviewSessionRecord> AccessPreviewSessions => Set<AccessPreviewSessionRecord>();
    public DbSet<PushSubscriptionRecord> PushSubscriptions => Set<PushSubscriptionRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Project>(entity =>
        {
            entity.HasIndex(project => project.ProgramName).IsUnique();
            entity.Property(project => project.ProgramName).HasMaxLength(160);
            entity.Property(project => project.ProgramManager).HasMaxLength(120);
            entity.Property(project => project.Engineer).HasMaxLength(120);
            entity.Property(project => project.CustomerName).HasMaxLength(160);
            entity.Property(project => project.SalesOrderNumber).HasMaxLength(80);
            entity.Property(project => project.JobNumber).HasMaxLength(80);
            entity.Property(project => project.Progress).HasPrecision(5, 4);
            entity.Property(project => project.Status).HasConversion<string>().HasMaxLength(24);
            entity.Property(project => project.CurrentTask).HasMaxLength(240);
            entity.Property(project => project.DeletedByAccountName).HasMaxLength(160);
            entity.Property(project => project.DeletedByDisplayName).HasMaxLength(160);
            entity.Property(project => project.Version).IsConcurrencyToken();
            entity.HasQueryFilter(project => project.DeletedAt == null);
        });

        modelBuilder.Entity<ProjectTask>(entity =>
        {
            entity.HasIndex(task => new { task.ProjectId, task.Sequence });
            entity.Property(task => task.ExternalTaskId).HasMaxLength(32);
            entity.Property(task => task.Title).HasMaxLength(240);
            entity.Property(task => task.Phase).HasMaxLength(120);
            entity.Property(task => task.WorkStation).HasMaxLength(120);
            entity.HasIndex(task => task.DependencyTaskId);
            entity.Property(task => task.PercentComplete).HasPrecision(5, 4);
            entity.Property(task => task.Status).HasConversion<string>().HasMaxLength(24);
            entity.Property(task => task.Notes).HasMaxLength(2000);
            entity.Property(task => task.Version).IsConcurrencyToken();
            entity.HasQueryFilter(task => task.Project.DeletedAt == null);
            entity.HasOne(task => task.Project)
                .WithMany(project => project.Tasks)
                .HasForeignKey(task => task.ProjectId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(task => task.DependencyTask)
                .WithMany(task => task.DependentTasks)
                .HasForeignKey(task => task.DependencyTaskId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<ProjectMessage>(entity =>
        {
            entity.HasIndex(message => new { message.ProjectId, message.CreatedAt });
            entity.Property(message => message.AuthorAccountName).HasMaxLength(160);
            entity.Property(message => message.AuthorDisplayName).HasMaxLength(160);
            entity.Property(message => message.Body).HasMaxLength(2000);
            entity.HasQueryFilter(message => message.Project.DeletedAt == null);
            entity.HasOne(message => message.Project)
                .WithMany(project => project.Messages)
                .HasForeignKey(message => message.ProjectId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ProjectAuditEntry>(entity =>
        {
            entity.HasIndex(entry => new { entry.ProjectId, entry.ChangedAt });
            entity.Property(entry => entry.Action).HasMaxLength(48);
            entity.Property(entry => entry.Summary).HasMaxLength(240);
            entity.Property(entry => entry.ChangedByAccountName).HasMaxLength(160);
            entity.Property(entry => entry.ChangedByDisplayName).HasMaxLength(160);
            entity.HasQueryFilter(entry => entry.Project.DeletedAt == null);
            entity.HasOne(entry => entry.Project)
                .WithMany(project => project.AuditEntries)
                .HasForeignKey(entry => entry.ProjectId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<UserNotification>(entity =>
        {
            entity.HasIndex(notification => new { notification.RecipientUserId, notification.ReadAt, notification.CreatedAt });
            entity.HasIndex(notification => notification.ProjectMessageId);
            entity.HasIndex(notification => notification.ProjectTaskId);
            entity.Property(notification => notification.Kind).HasConversion<string>().HasMaxLength(40);
            entity.Property(notification => notification.ActorAccountName).HasMaxLength(160);
            entity.Property(notification => notification.ActorDisplayName).HasMaxLength(160);
            entity.Property(notification => notification.Title).HasMaxLength(240);
            entity.Property(notification => notification.BodyPreview).HasMaxLength(320);
            entity.HasQueryFilter(notification => notification.Project.DeletedAt == null);
            entity.HasOne(notification => notification.RecipientUser)
                .WithMany(user => user.Notifications)
                .HasForeignKey(notification => notification.RecipientUserId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(notification => notification.Project)
                .WithMany(project => project.Notifications)
                .HasForeignKey(notification => notification.ProjectId)
                .OnDelete(DeleteBehavior.NoAction);
            entity.HasOne(notification => notification.ProjectTask)
                .WithMany(task => task.Notifications)
                .HasForeignKey(notification => notification.ProjectTaskId)
                .OnDelete(DeleteBehavior.SetNull);
            entity.HasOne(notification => notification.ProjectMessage)
                .WithMany(message => message.Notifications)
                .HasForeignKey(notification => notification.ProjectMessageId)
                .OnDelete(DeleteBehavior.NoAction);
        });

        modelBuilder.Entity<TaskOvertimeDay>(entity =>
        {
            entity.HasIndex(day => new { day.ProjectTaskId, day.Date }).IsUnique();
            entity.Property(day => day.Note).HasMaxLength(240);
            entity.HasQueryFilter(day => day.ProjectTask.Project.DeletedAt == null);
            entity.HasOne(day => day.ProjectTask)
                .WithMany(task => task.OvertimeDays)
                .HasForeignKey(day => day.ProjectTaskId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ScheduleSettings>(entity =>
        {
            entity.Property(settings => settings.Id).ValueGeneratedNever();
        });

        modelBuilder.Entity<Phase>(entity =>
        {
            entity.HasIndex(phase => phase.Name).IsUnique();
            entity.Property(phase => phase.Name).HasMaxLength(120);
        });

        modelBuilder.Entity<Holiday>(entity =>
        {
            entity.HasIndex(holiday => holiday.Date).IsUnique();
            entity.Property(holiday => holiday.Name).HasMaxLength(160);
        });

        modelBuilder.Entity<WorkCenter>(entity =>
        {
            entity.HasIndex(workCenter => workCenter.Name).IsUnique();
            entity.Property(workCenter => workCenter.Name).HasMaxLength(120);
        });

        modelBuilder.Entity<AppUser>(entity =>
        {
            entity.HasIndex(user => user.AccountName).IsUnique();
            entity.Property(user => user.AccountName).HasMaxLength(160);
            entity.Property(user => user.DisplayName).HasMaxLength(160);
            // Kept as a compatibility bridge for the Hub and Engineering module.
            entity.Property<string>("Role").HasMaxLength(32).HasDefaultValue("Viewer").IsRequired();
            entity.HasMany(user => user.GroupMemberships)
                .WithOne(membership => membership.User)
                .HasForeignKey(membership => membership.AppUserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<AppGroup>(entity =>
        {
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

        modelBuilder.Entity<AppUserGroupMembership>(entity =>
        {
            entity.HasKey(membership => new { membership.AppUserId, membership.AppGroupId });
            entity.HasIndex(membership => membership.AppGroupId);
        });

        modelBuilder.Entity<AppUserModuleAccess>(entity =>
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

        modelBuilder.Entity<AppGroupPermission>(entity =>
        {
            entity.HasKey(permission => new { permission.AppGroupId, permission.PermissionKey });
            entity.Property(permission => permission.PermissionKey).HasMaxLength(120);
        });

        modelBuilder.Entity<StatusHistory>(entity =>
        {
            entity.Property(history => history.EntityName).HasMaxLength(240);
            entity.Property(history => history.OldStatus).HasMaxLength(32);
            entity.Property(history => history.NewStatus).HasMaxLength(32);
            entity.Property(history => history.ChangedBy).HasMaxLength(160);
            entity.HasQueryFilter(history => history.Project.DeletedAt == null);
            entity.HasOne(history => history.Project)
                .WithMany()
                .HasForeignKey(history => history.ProjectId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(history => history.ProjectTask)
                .WithMany()
                .HasForeignKey(history => history.ProjectTaskId)
                .OnDelete(DeleteBehavior.NoAction);
        });

        modelBuilder.Entity<PushSubscriptionRecord>(entity =>
        {
            entity.ToTable("PushSubscriptions");
            entity.HasIndex(subscription => subscription.Endpoint).IsUnique();
            entity.HasIndex(subscription => subscription.AppUserId);
            entity.Property(subscription => subscription.Endpoint).HasMaxLength(2048).IsRequired();
            entity.Property(subscription => subscription.P256dh).HasMaxLength(256).IsRequired();
            entity.Property(subscription => subscription.Auth).HasMaxLength(128).IsRequired();
            entity.HasOne(subscription => subscription.User)
                .WithMany(user => user.PushSubscriptions)
                .HasForeignKey(subscription => subscription.AppUserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<AccessPreviewSessionRecord>(entity =>
        {
            entity.ToTable("AccessPreviewSessions");
            entity.HasKey(session => session.Id);
            entity.Property(session => session.Id).ValueGeneratedNever();
            entity.Property(session => session.TokenHash).HasMaxLength(64).IsRequired();
            entity.Property(session => session.AdministratorAccountName).HasMaxLength(160).IsRequired();
            entity.Property(session => session.TargetKey).HasMaxLength(160).IsRequired();
            entity.Property(session => session.ApplicationId).HasMaxLength(80).IsRequired();
            entity.HasIndex(session => session.TokenHash).IsUnique();
            entity.HasIndex(session => new { session.ApplicationId, session.SessionExpiresAt });
        });
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        ApplyLegacyUserRoleDefaults();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(
        bool acceptAllChangesOnSuccess,
        CancellationToken cancellationToken = default)
    {
        ApplyLegacyUserRoleDefaults();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    public void SetLegacyRole(AppUser user, string role)
    {
        Entry(user).Property<string>("Role").CurrentValue = role;
    }

    private void ApplyLegacyUserRoleDefaults()
    {
        foreach (var entry in ChangeTracker.Entries<AppUser>().Where(entry => entry.State == EntityState.Added))
        {
            if (string.IsNullOrWhiteSpace(entry.Property<string>("Role").CurrentValue))
            {
                entry.Property<string>("Role").CurrentValue = "Viewer";
            }
        }
    }
}

