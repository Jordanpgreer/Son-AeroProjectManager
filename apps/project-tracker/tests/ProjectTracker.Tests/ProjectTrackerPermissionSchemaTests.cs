using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using ProjectTracker.Api.Auth;
using ProjectTracker.Api.Data;
using ProjectTracker.Api.Models;
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
    }

    [Fact]
    public void NotificationSourceRelationships_UseSetNull()
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
            DeleteBehavior.SetNull,
            notification.GetForeignKeys().Single(key => key.Properties.Single().Name == nameof(UserNotification.ProjectMessageId)).DeleteBehavior);
        Assert.Equal(80, db.Model.FindEntityType(typeof(Project))!.FindProperty(nameof(Project.JobNumber))!.GetMaxLength());
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
