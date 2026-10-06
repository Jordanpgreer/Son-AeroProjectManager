using Microsoft.EntityFrameworkCore;
using ProjectTracker.Api.Data;
using ProjectTracker.Api.Models;

namespace ProjectTracker.Api.Services;

public sealed record ProjectTaskReferenceCleanupResult(
    int DependenciesCleared,
    int StatusHistoryDetached,
    int NotificationsDetached);

public sealed class ProjectTaskReferenceCleanupService(ProjectTrackerDbContext db)
{
    public async Task<ProjectTaskReferenceCleanupResult> PrepareForRemovalAsync(
        IReadOnlyCollection<ProjectTask> tasksToRemove,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var persistedTaskIds = tasksToRemove
            .Where(task => task.Id > 0)
            .Select(task => task.Id)
            .ToHashSet();
        if (persistedTaskIds.Count == 0)
            return new ProjectTaskReferenceCleanupResult(0, 0, 0);

        var dependents = await db.Tasks
            .IgnoreQueryFilters()
            .Where(task => task.DependencyTaskId.HasValue
                && persistedTaskIds.Contains(task.DependencyTaskId.Value))
            .ToListAsync(cancellationToken);
        var dependenciesCleared = 0;
        foreach (var dependent in dependents)
        {
            dependent.DependencyTaskId = null;
            dependent.DependencyTask = null;
            if (persistedTaskIds.Contains(dependent.Id)) continue;

            dependent.Version++;
            dependent.UpdatedAt = now;
            dependenciesCleared++;
        }

        var statusHistory = await db.StatusHistory
            .IgnoreQueryFilters()
            .Where(history => history.ProjectTaskId.HasValue
                && persistedTaskIds.Contains(history.ProjectTaskId.Value))
            .ToListAsync(cancellationToken);
        foreach (var history in statusHistory)
        {
            history.ProjectTaskId = null;
            history.ProjectTask = null;
        }

        var notifications = await db.UserNotifications
            .IgnoreQueryFilters()
            .Where(notification => notification.ProjectTaskId.HasValue
                && persistedTaskIds.Contains(notification.ProjectTaskId.Value))
            .ToListAsync(cancellationToken);
        foreach (var notification in notifications)
        {
            notification.ProjectTaskId = null;
            notification.ProjectTask = null;
        }

        return new ProjectTaskReferenceCleanupResult(
            dependenciesCleared,
            statusHistory.Count,
            notifications.Count);
    }
}
