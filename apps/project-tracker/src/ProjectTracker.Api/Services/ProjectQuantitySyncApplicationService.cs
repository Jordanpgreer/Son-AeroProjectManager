using Microsoft.EntityFrameworkCore;
using ProjectTracker.Api.Data;
using ProjectTracker.Api.Dtos;
using ProjectTracker.Api.Mapping;
using ProjectTracker.Api.Models;

namespace ProjectTracker.Api.Services;

/// <summary>Applies a validated ERP snapshot atomically for interactive and scheduled pulls.</summary>
public sealed class ProjectQuantitySyncApplicationService(
    ProjectTrackerDbContext db,
    ProjectAuditService audit,
    ProjectRoutingSyncService routingSync,
    ProjectTaskReferenceCleanupService taskReferenceCleanup,
    ProjectMetricsService metrics,
    OperationScheduleReminderService reminders)
{
    public async Task<ProjectQuantitySyncResultDto> ApplyAsync(
        Project project,
        ProjectQuantitySnapshot snapshot,
        string provider,
        bool preserveQuantities,
        ProjectRoutingSyncMode routingMode,
        CancellationToken cancellationToken,
        bool scheduled = false)
    {
        if (project.DeletedAt is not null || project.Status == ProjectStatus.Complete)
            throw new InvalidOperationException("Only active projects can receive ERP updates.");
        if (scheduled && routingMode != ProjectRoutingSyncMode.PopulateWhenBlank)
            throw new InvalidOperationException("Scheduled synchronization cannot reset project routing.");
        if (!snapshot.MatchConfirmed)
            return new(ProjectDtoMapper.ToDetailDto(project), provider, [], [], snapshot.Warnings);

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var before = ProjectAuditService.CaptureProject(project);
        var pulled = new List<string>();
        var retained = new List<string>();
        if (!preserveQuantities && snapshot.RequiredQuantity is not null
            && (!scheduled || project.RequiredQuantity is null
                || project.RequiredQuantitySource != ProjectQuantitySources.Manual))
        {
            project.RequiredQuantity = snapshot.RequiredQuantity;
            project.RequiredQuantitySource = provider;
            pulled.Add("Required quantity");
        }
        else if (project.RequiredQuantity is not null) retained.Add("Required quantity");

        if (!preserveQuantities && snapshot.JobQuantity is not null
            && (!scheduled || project.JobQuantity is null
                || project.JobQuantitySource != ProjectQuantitySources.Manual))
        {
            project.JobQuantity = snapshot.JobQuantity;
            project.JobQuantitySource = provider;
            pulled.Add("Job quantity");
        }
        else if (project.JobQuantity is not null) retained.Add("Job quantity");

        var syncTime = DateTimeOffset.UtcNow;
        var routingResult = routingSync.Apply(project, snapshot.ConfirmedRoutingSteps, provider, syncTime, routingMode);
        if (routingResult.RemovedTasks.Count > 0)
        {
            await taskReferenceCleanup.PrepareForRemovalAsync(routingResult.RemovedTasks, syncTime, cancellationToken);
            db.Tasks.RemoveRange(routingResult.RemovedTasks);
        }
        var routingChanged = routingResult.Added > 0 || routingResult.Updated > 0
            || routingResult.ProgressUpdated > 0 || routingResult.Removed > 0;
        var dataChanged = routingChanged
            || ProjectAuditService.Diff(before, ProjectAuditService.CaptureProject(project)).Count > 0;
        var warnings = snapshot.Warnings.Concat(routingResult.Warnings)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        project.QuantityLastSyncProvider = provider;
        project.QuantityLastSyncedAt = syncTime;
        // A successful unchanged scheduled pull updates freshness without prompting every
        // open editor to reload or adding an audit entry every twenty minutes.
        if (!scheduled || dataChanged)
        {
            project.UpdatedAt = syncTime;
            project.Version++;
        }
        if (routingChanged)
            await metrics.RefreshProjectAsync(db, project, cancellationToken, preserveTaskSchedule: true);

        if (!scheduled || dataChanged)
        {
            var changes = ProjectAuditService.Diff(before, ProjectAuditService.CaptureProject(project)).ToList();
            if (routingResult.Added > 0)
                changes.Add(new ProjectAuditChange("Routing operations added", null, routingResult.Added.ToString()));
            if (routingResult.Updated > 0)
                changes.Add(new ProjectAuditChange("Routing operations updated", null, routingResult.Updated.ToString()));
            if (routingResult.ProgressUpdated > 0)
                changes.Add(new ProjectAuditChange($"{provider} operation progress updated", null, routingResult.ProgressUpdated.ToString()));
            if (routingResult.ArdaOnlyRetained > 0)
                changes.Add(new ProjectAuditChange("Arda-only operations retained", null, routingResult.ArdaOnlyRetained.ToString()));
            if (routingResult.Removed > 0)
                changes.Add(new ProjectAuditChange("Operations removed by routing reset", null, routingResult.Removed.ToString()));
            audit.Record(db, project,
                routingMode == ProjectRoutingSyncMode.ForceOverride ? "ProjectRoutingReset" : "ProjectQuantitySync",
                routingMode == ProjectRoutingSyncMode.ForceOverride
                    ? $"Reset this project's operations and quantities from {provider}"
                    : $"Refreshed project quantities from {provider}",
                changes,
                actorAccountName: scheduled ? "ARDA-FULCRUM-SYNC" : null,
                actorDisplayName: scheduled ? "Arda automatic sync" : null);
        }
        await db.SaveChangesAsync(cancellationToken);

        var startedExternalIds = snapshot.ConfirmedRoutingSteps
            .Where(step => step.ActualStartDate is not null || step.IsComplete)
            .Select(step => step.ExternalId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var completedExternalIds = snapshot.ConfirmedRoutingSteps
            .Where(step => step.ActualCompletionDate is not null || step.IsComplete)
            .Select(step => step.ExternalId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var startedTaskIds = project.Tasks
            .Where(task => task.Id > 0 && task.ExternalSourceOperationId is not null
                && startedExternalIds.Contains(task.ExternalSourceOperationId))
            .Select(task => task.Id).ToArray();
        var completedTaskIds = project.Tasks
            .Where(task => task.Id > 0 && task.ExternalSourceOperationId is not null
                && completedExternalIds.Contains(task.ExternalSourceOperationId))
            .Select(task => task.Id).ToArray();
        await reminders.ResolveFromExternalProgressAsync(startedTaskIds, completedTaskIds, cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return new(ProjectDtoMapper.ToDetailDto(project), provider, pulled, retained, warnings,
            routingResult.Added, routingResult.Updated, routingResult.ArdaOnlyRetained,
            routingResult.Removed, routingResult.PreservedExisting, routingResult.ProgressUpdated,
            routingResult.ResetApplied);
    }
}
