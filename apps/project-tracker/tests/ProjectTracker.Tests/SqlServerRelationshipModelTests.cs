using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using System.Reflection;
using ProjectTracker.Api.Data;
using ProjectTracker.Api.Data.Migrations;
using ProjectTracker.Api.Models;
using ProjectTracker.Api.Services;

namespace ProjectTracker.Tests;

public sealed class SqlServerRelationshipModelTests
{
    private static readonly string[] HandwrittenMigrationIds =
    [
        "20260626120000_AddStartDateLocked",
        "20260626121000_AddPercentCompleteManual",
        "20260626122000_AddWorkCenters",
        "20260626133000_AddProjectCustomerSalesOrder",
        "20260628143000_AddTaskDependency",
        "20260629224500_AddTaskNoteUpdatedAt"
    ];

    [Fact]
    public void UserNotificationForeignKeys_AvoidSqlServerMultipleCascadePaths()
    {
        var options = new DbContextOptionsBuilder<ProjectTrackerDbContext>()
            .UseSqlServer("Server=(local);Database=ModelValidation;Integrated Security=True;TrustServerCertificate=True")
            .Options;
        using var db = new ProjectTrackerDbContext(options);
        var notification = db.Model.FindEntityType(typeof(UserNotification));

        Assert.NotNull(notification);
        Assert.Equal(DeleteBehavior.NoAction, FindForeignKey(nameof(UserNotification.ProjectId)).DeleteBehavior);
        Assert.Equal(DeleteBehavior.NoAction, FindForeignKey(nameof(UserNotification.ProjectMessageId)).DeleteBehavior);
        Assert.Equal(DeleteBehavior.SetNull, FindForeignKey(nameof(UserNotification.ProjectTaskId)).DeleteBehavior);
        Assert.Equal(DeleteBehavior.Cascade, FindForeignKey(nameof(UserNotification.RecipientUserId)).DeleteBehavior);

        Microsoft.EntityFrameworkCore.Metadata.IReadOnlyForeignKey FindForeignKey(string propertyName) =>
            Assert.Single(notification!.GetForeignKeys(), foreignKey =>
                foreignKey.Properties.Any(property => property.Name == propertyName));
    }

    [Fact]
    public void NotificationMigration_UsesTheSameSafeDeleteActions()
    {
        var builder = new MigrationBuilder("Microsoft.EntityFrameworkCore.SqlServer");
        new TestableNotificationMigration().Build(builder);
        var table = Assert.Single(builder.Operations.OfType<CreateTableOperation>(), operation =>
            operation.Name == "UserNotifications");

        Assert.Equal(ReferentialAction.NoAction, FindForeignKey("FK_UserNotifications_Projects_ProjectId").OnDelete);
        Assert.Equal(ReferentialAction.NoAction, FindForeignKey("FK_UserNotifications_ProjectMessages_ProjectMessageId").OnDelete);
        Assert.Equal(ReferentialAction.SetNull, FindForeignKey("FK_UserNotifications_Tasks_ProjectTaskId").OnDelete);
        Assert.Equal(ReferentialAction.Cascade, FindForeignKey("FK_UserNotifications_Users_RecipientUserId").OnDelete);

        AddForeignKeyOperation FindForeignKey(string name) =>
            Assert.Single(table.ForeignKeys, foreignKey => foreignKey.Name == name);
    }

    [Fact]
    public void EveryMigration_HasDiscoveryMetadata()
    {
        var migrationTypes = typeof(ProjectTrackerDbContext).Assembly.GetTypes()
            .Where(type => !type.IsAbstract && typeof(Migration).IsAssignableFrom(type))
            .ToArray();

        var missingMetadata = migrationTypes
            .Where(type => type.GetCustomAttribute<MigrationAttribute>() is null
                || type.GetCustomAttribute<DbContextAttribute>()?.ContextType != typeof(ProjectTrackerDbContext))
            .Select(type => type.FullName)
            .ToArray();

        Assert.NotEmpty(migrationTypes);
        Assert.Empty(missingMetadata);
    }

    [Fact]
    public void ExternalLinksMigration_AddsOnlyNullableBoundedProjectColumns()
    {
        var builder = new MigrationBuilder("Microsoft.EntityFrameworkCore.SqlServer");
        new TestableExternalLinksMigration().Build(builder);

        var columns = builder.Operations.OfType<AddColumnOperation>().ToList();
        Assert.Collection(
            columns.OrderBy(column => column.Name),
            column =>
            {
                Assert.Equal("JobUrl", column.Name);
                Assert.Equal("Projects", column.Table);
                Assert.True(column.IsNullable);
                Assert.Equal(ProjectExternalLinks.MaxLength, column.MaxLength);
            },
            column =>
            {
                Assert.Equal("SalesOrderUrl", column.Name);
                Assert.Equal("Projects", column.Table);
                Assert.True(column.IsNullable);
                Assert.Equal(ProjectExternalLinks.MaxLength, column.MaxLength);
            });
        Assert.Equal(2, builder.Operations.Count);
    }

    [Fact]
    public void HandwrittenMigrations_AreDiscoverableBeforeDependencyEnforcement()
    {
        var options = new DbContextOptionsBuilder<ProjectTrackerDbContext>()
            .UseSqlServer("Server=(local);Database=MigrationDiscovery;Integrated Security=True;TrustServerCertificate=True")
            .Options;
        using var db = new ProjectTrackerDbContext(options);
        var migrations = db.Database.GetMigrations().ToArray();
        var enforcementIndex = Array.IndexOf(
            migrations,
            "20260723223402_EnforceExplicitProgressAndTaskDependencies");

        Assert.True(enforcementIndex >= 0);
        foreach (var migrationId in HandwrittenMigrationIds)
        {
            var migrationIndex = Array.IndexOf(migrations, migrationId);
            Assert.True(migrationIndex >= 0, $"Migration {migrationId} is not discoverable.");
            Assert.True(
                migrationIndex < enforcementIndex,
                $"Migration {migrationId} must run before dependency enforcement.");
        }
    }

    private sealed class TestableNotificationMigration : AddNotificationsJobNumberAndActivityPermission
    {
        public void Build(MigrationBuilder builder) => Up(builder);
    }

    private sealed class TestableExternalLinksMigration : AddProjectExternalLinks
    {
        public void Build(MigrationBuilder builder) => Up(builder);
    }
}
