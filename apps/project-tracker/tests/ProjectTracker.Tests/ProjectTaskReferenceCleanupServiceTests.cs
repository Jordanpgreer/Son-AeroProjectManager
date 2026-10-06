using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using ProjectTracker.Api.Data;
using ProjectTracker.Api.Models;
using ProjectTracker.Api.Services;

namespace ProjectTracker.Tests;

public sealed class ProjectTaskReferenceCleanupServiceTests
{
    [Fact]
    public async Task PrepareForRemovalAsync_detaches_all_operation_references_before_delete()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<ProjectTrackerDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new ProjectTrackerDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var removedTask = new ProjectTask { Sequence = 1, Title = "Cut", Version = 4 };
        var dependentTask = new ProjectTask
        {
            Sequence = 2,
            Title = "Weld",
            DependencyTask = removedTask,
            Version = 7
        };
        var project = new Project
        {
            ProgramName = "REFERENCE-CLEANUP-TEST",
            Tasks = [removedTask, dependentTask]
        };
        var recipient = new AppUser
        {
            AccountName = @"TEST\Recipient",
            DisplayName = "Recipient"
        };
        var history = new StatusHistory
        {
            Project = project,
            ProjectTask = removedTask,
            EntityName = "Cut",
            OldStatus = "NotStarted",
            NewStatus = "InProgress",
            ChangedBy = @"TEST\Admin"
        };
        var notification = new UserNotification
        {
            RecipientUser = recipient,
            Project = project,
            ProjectTask = removedTask,
            Kind = NotificationKind.OperationStartConfirmation,
            ActorAccountName = @"TEST\Admin",
            ActorDisplayName = "Admin",
            Title = "Did Cut start?",
            BodyPreview = "Confirm the operation start."
        };
        db.AddRange(project, recipient, history, notification);
        await db.SaveChangesAsync();
        var removedTaskId = removedTask.Id;
        var dependentTaskId = dependentTask.Id;
        var historyId = history.Id;
        var notificationId = notification.Id;
        db.ChangeTracker.Clear();

        var persistedTask = await db.Tasks.SingleAsync(task => task.Id == removedTaskId);
        var service = new ProjectTaskReferenceCleanupService(db);
        var result = await service.PrepareForRemovalAsync(
            [persistedTask],
            DateTimeOffset.UtcNow,
            CancellationToken.None);
        db.Tasks.Remove(persistedTask);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        Assert.Equal(1, result.DependenciesCleared);
        Assert.Equal(1, result.StatusHistoryDetached);
        Assert.Equal(1, result.NotificationsDetached);
        Assert.False(await db.Tasks.AnyAsync(task => task.Id == removedTaskId));
        var retainedTask = await db.Tasks.SingleAsync(task => task.Id == dependentTaskId);
        Assert.Null(retainedTask.DependencyTaskId);
        Assert.Equal(8, retainedTask.Version);
        Assert.Null((await db.StatusHistory.SingleAsync(row => row.Id == historyId)).ProjectTaskId);
        Assert.Null((await db.UserNotifications.SingleAsync(row => row.Id == notificationId)).ProjectTaskId);
    }

    [Fact]
    public async Task Force_override_can_replace_a_persisted_route_with_linked_history_and_notifications()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<ProjectTrackerDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new ProjectTrackerDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var first = new ProjectTask { Sequence = 1, Title = "Old Cut" };
        var second = new ProjectTask
        {
            Sequence = 2,
            Title = "Old Weld",
            DependencyTask = first
        };
        var project = new Project
        {
            ProgramName = "ROUTE-RESET-TEST",
            Tasks = [first, second]
        };
        var recipient = new AppUser
        {
            AccountName = @"TEST\ResetRecipient",
            DisplayName = "Reset Recipient"
        };
        db.AddRange(
            project,
            recipient,
            new StatusHistory
            {
                Project = project,
                ProjectTask = first,
                EntityName = "Old Cut",
                OldStatus = "NotStarted",
                NewStatus = "InProgress",
                ChangedBy = @"TEST\Admin"
            },
            new UserNotification
            {
                RecipientUser = recipient,
                Project = project,
                ProjectTask = second,
                Kind = NotificationKind.OperationFinishConfirmation,
                ActorAccountName = @"TEST\Admin",
                ActorDisplayName = "Admin",
                Title = "Did Old Weld finish?",
                BodyPreview = "Confirm the operation finish."
            });
        await db.SaveChangesAsync();
        var oldTaskIds = project.Tasks.Select(task => task.Id).ToArray();
        db.ChangeTracker.Clear();

        project = await db.Projects
            .Include(candidate => candidate.Tasks)
            .SingleAsync(candidate => candidate.Id == project.Id);
        var routingResult = new ProjectRoutingSyncService().Apply(
            project,
            [
                new ProjectRoutingStepSnapshot("new-cut", 10, "Cut"),
                new ProjectRoutingStepSnapshot("new-inspect", 20, "Inspect")
            ],
            "Fulcrum",
            DateTimeOffset.UtcNow,
            ProjectRoutingSyncMode.ForceOverride);
        var cleanup = new ProjectTaskReferenceCleanupService(db);
        await cleanup.PrepareForRemovalAsync(
            routingResult.RemovedTasks,
            DateTimeOffset.UtcNow,
            CancellationToken.None);
        db.Tasks.RemoveRange(routingResult.RemovedTasks);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        Assert.True(routingResult.ResetApplied);
        Assert.Equal(2, routingResult.Added);
        Assert.Equal(2, routingResult.Removed);
        Assert.False(await db.Tasks.AnyAsync(task => oldTaskIds.Contains(task.Id)));
        Assert.Equal(
            new[] { "Cut", "Inspect" },
            await db.Tasks
                .Where(task => task.ProjectId == project.Id)
                .OrderBy(task => task.Sequence)
                .Select(task => task.Title)
                .ToArrayAsync());
        Assert.All(await db.StatusHistory.ToListAsync(), history => Assert.Null(history.ProjectTaskId));
        Assert.All(await db.UserNotifications.ToListAsync(), notification => Assert.Null(notification.ProjectTaskId));
    }
}
