using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using ProjectTracker.Api.Auth;
using ProjectTracker.Api.Data;
using ProjectTracker.Api.Models;
using ProjectTracker.Api.Services;
using ProjectTracker.Api.Endpoints;
using SonAero.Platform.Security;

namespace ProjectTracker.Tests;

public sealed class ProjectTrackerPermissionSchemaTests
{
    [Fact]
    public void LocalPermissions_AreAvailableToTheAdminGroupEditor()
    {
        Assert.Contains(ProjectTrackerPermissions.All, permission =>
            permission.Key == ProjectTrackerPermissions.ProjectActivityView
            && permission.Label == "View Project Activity");
        Assert.Contains(ProjectTrackerPermissions.AllKeys, key => key == ProjectTrackerPermissions.ProjectEditJobNumber);
        Assert.Contains(ProjectTrackerPermissions.All, permission =>
            permission.Key == ProjectTrackerPermissions.ProjectEditExternalLinks
            && permission.Label == "Edit SO / Job Links");
        Assert.Contains(
            ProjectTrackerPermissions.ProjectEditExternalLinks,
            ProjectTrackerPermissions.DefaultsForGroup(ApplicationGroups.Administrators));
        Assert.DoesNotContain(
            ProjectTrackerPermissions.ProjectEditExternalLinks,
            ProjectTrackerPermissions.DefaultsForGroup(ApplicationGroups.Managers));
        Assert.Contains(ProjectTrackerPermissions.All, permission =>
            permission.Key == ProjectTrackerPermissions.ArchivedDelete
            && permission.Label == "Permanently Delete Archived Projects");
        Assert.Contains(
            ProjectTrackerPermissions.ArchivedDelete,
            ProjectTrackerPermissions.DefaultsForGroup(ApplicationGroups.Administrators));
        Assert.DoesNotContain(
            ProjectTrackerPermissions.ArchivedDelete,
            ProjectTrackerPermissions.DefaultsForGroup(ApplicationGroups.Managers));
        Assert.Contains(ProjectTrackerPermissions.All, permission =>
            permission.Key == ProjectTrackerPermissions.OperationScheduleConfirm
            && permission.Label == "Operation Start / Finish Prompts");
        Assert.Contains(
            ProjectTrackerPermissions.OperationScheduleConfirm,
            ProjectTrackerPermissions.DefaultsForGroup(ApplicationGroups.Managers));
        Assert.Contains(
            ProjectTrackerPermissions.OperationScheduleConfirm,
            ProjectTrackerPermissions.DefaultsForGroup(ApplicationGroups.Engineering));
        Assert.Contains(ProjectTrackerPermissions.All, permission =>
            permission.Key == ProjectTrackerPermissions.WorkCentersImport
            && permission.Label == "Import Work Centers");
        Assert.Contains(
            ProjectTrackerPermissions.WorkCentersImport,
            ProjectTrackerPermissions.DefaultsForGroup(ApplicationGroups.Administrators));
        Assert.DoesNotContain(
            ProjectTrackerPermissions.WorkCentersImport,
            ProjectTrackerPermissions.DefaultsForGroup(ApplicationGroups.Managers));
        Assert.True(UserEndpoints.CanHoldAdministratorOnlyPermissions(
            "Custom Work Center Importers",
            [ApplicationPermissions.ModuleView, ProjectTrackerPermissions.WorkCentersImport]));
    }

    [Fact]
    public void ArchivedDeletePermission_CannotBeAssignedOutsideTheAdministratorsGroup()
    {
        Assert.True(UserEndpoints.CanHoldAdministratorOnlyPermissions(
            ApplicationGroups.Administrators,
            [ProjectTrackerPermissions.ArchivedDelete]));
        Assert.False(UserEndpoints.CanHoldAdministratorOnlyPermissions(
            ApplicationGroups.Managers,
            [ProjectTrackerPermissions.ArchivedDelete]));
        Assert.False(UserEndpoints.CanHoldAdministratorOnlyPermissions(
            "Custom Admin-Like Group",
            [ApplicationPermissions.ModuleView, ProjectTrackerPermissions.ArchivedDelete]));
    }

    [Fact]
    public void NotificationSourceRelationships_UseSetNullOnlyWhereSqlServerAllowsIt()
    {
        var options = new DbContextOptionsBuilder<ProjectTrackerDbContext>()
            .UseSqlite("Data Source=:memory:")
            .Options;
        using var db = new ProjectTrackerDbContext(options);
        var notification = db.Model.FindEntityType(typeof(UserNotification))!;

        Assert.Equal(
            DeleteBehavior.SetNull,
            notification.GetForeignKeys().Single(key => key.Properties.Single().Name == nameof(UserNotification.ProjectTaskId)).DeleteBehavior);
        Assert.Equal(
            DeleteBehavior.NoAction,
            notification.GetForeignKeys().Single(key => key.Properties.Single().Name == nameof(UserNotification.ProjectMessageId)).DeleteBehavior);
        Assert.Equal(80, db.Model.FindEntityType(typeof(Project))!.FindProperty(nameof(Project.JobNumber))!.GetMaxLength());
        Assert.Equal(ProjectExternalLinks.MaxLength, db.Model.FindEntityType(typeof(Project))!.FindProperty(nameof(Project.SalesOrderUrl))!.GetMaxLength());
        Assert.Equal(ProjectExternalLinks.MaxLength, db.Model.FindEntityType(typeof(Project))!.FindProperty(nameof(Project.JobUrl))!.GetMaxLength());
        var reminderIndex = notification.GetIndexes().Single(index =>
            index.Properties.Select(property => property.Name).SequenceEqual([
                nameof(UserNotification.RecipientUserId),
                nameof(UserNotification.ProjectTaskId),
                nameof(UserNotification.Kind),
                nameof(UserNotification.ScheduledDate)]));
        Assert.True(reminderIndex.IsUnique);
        Assert.Equal("[ScheduledDate] IS NOT NULL", reminderIndex.GetFilter());
    }

    [Fact]
    public void UserModuleAccess_UsesACompositeKeyAndNullableRole()
    {
        var options = new DbContextOptionsBuilder<ProjectTrackerDbContext>()
            .UseSqlite("Data Source=:memory:")
            .Options;
        using var db = new ProjectTrackerDbContext(options);
        var access = db.Model.FindEntityType(typeof(AppUserModuleAccess))!;

        Assert.Equal(
            [nameof(AppUserModuleAccess.AppUserId), nameof(AppUserModuleAccess.ModuleKey)],
            access.FindPrimaryKey()!.Properties.Select(property => property.Name));
        Assert.True(access.FindProperty(nameof(AppUserModuleAccess.Role))!.IsNullable);
        Assert.Equal(40, access.FindProperty(nameof(AppUserModuleAccess.ModuleKey))!.GetMaxLength());
    }

    [Fact]
    public async Task SqliteDefaultPermissionSeed_RunsOnlyOnce()
    {
        var path = Path.Combine(Path.GetTempPath(), $"project-tracker-permissions-{Guid.NewGuid():N}.db");
        ProjectTrackerDbContext? db = null;
        try
        {
            var options = new DbContextOptionsBuilder<ProjectTrackerDbContext>()
                .UseSqlite($"Data Source={path}")
                .Options;
            db = new ProjectTrackerDbContext(options);
            await db.Database.EnsureCreatedAsync();
            var group = new AppGroup
            {
                Name = "Test Group",
                Permissions =
                [
                    new AppGroupPermission { PermissionKey = ApplicationPermissions.ModuleView },
                    new AppGroupPermission { PermissionKey = ApplicationPermissions.ProjectEditSalesOrderNumber }
                ]
            };
            db.Groups.Add(group);
            await db.SaveChangesAsync();

            await SqliteCompatibility.EnsureLocalPermissionSeedAsync(db, CancellationToken.None);
            Assert.Contains(await db.GroupPermissions.AsNoTracking().ToListAsync(), permission =>
                permission.PermissionKey == ProjectTrackerPermissions.ProjectActivityView);
            Assert.Contains(await db.GroupPermissions.AsNoTracking().ToListAsync(), permission =>
                permission.PermissionKey == ProjectTrackerPermissions.ProjectEditJobNumber);

            var activity = await db.GroupPermissions.SingleAsync(permission =>
                permission.PermissionKey == ProjectTrackerPermissions.ProjectActivityView);
            db.GroupPermissions.Remove(activity);
            await db.SaveChangesAsync();

            await SqliteCompatibility.EnsureLocalPermissionSeedAsync(db, CancellationToken.None);
            Assert.DoesNotContain(await db.GroupPermissions.AsNoTracking().ToListAsync(), permission =>
                permission.PermissionKey == ProjectTrackerPermissions.ProjectActivityView);
        }
        finally
        {
            if (db is not null)
            {
                await db.DisposeAsync();
            }
            SqliteConnection.ClearAllPools();
            File.Delete(path);
        }
    }
}
