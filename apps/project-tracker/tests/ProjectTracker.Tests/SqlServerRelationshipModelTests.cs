using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using ProjectTracker.Api.Data;
using ProjectTracker.Api.Data.Migrations;
using ProjectTracker.Api.Models;

namespace ProjectTracker.Tests;

public sealed class SqlServerRelationshipModelTests
{
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

    private sealed class TestableNotificationMigration : AddNotificationsJobNumberAndActivityPermission
    {
        public void Build(MigrationBuilder builder) => Up(builder);
    }
}
