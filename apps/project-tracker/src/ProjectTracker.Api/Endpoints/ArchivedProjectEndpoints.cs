using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ProjectTracker.Api.Data;
using ProjectTracker.Api.Dtos;
using ProjectTracker.Api.Models;
using ProjectTracker.Api.Services;
using ProjectTracker.Api.Auth;
using ProjectTracker.Api.Services.Import;
using SonAero.Platform.Security;

namespace ProjectTracker.Api.Endpoints;

public static class ArchivedProjectEndpoints
{
    public const string PermanentDeletePolicyName = "PermanentlyDeleteArchived";

    public static void ConfigurePermanentDeletePolicy(AuthorizationPolicyBuilder policy)
    {
        policy
            .RequireClaim(ApplicationClaimTypes.Group, ApplicationGroups.Administrators)
            .RequireClaim(ApplicationClaimTypes.Permission, ProjectTrackerPermissions.ArchivedDelete);
    }

    public static RouteGroupBuilder MapArchivedProjectEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/archived-projects", async (ProjectTrackerDbContext db, CancellationToken cancellationToken) =>
        {
            var projects = await db.Projects
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(project => project.DeletedAt != null)
                .ToListAsync(cancellationToken);
            return projects
                .OrderByDescending(project => project.DeletedAt)
                .Select(project => new ArchivedProjectDto(
                    project.Id,
                    project.Version,
                    project.ProgramName,
                    project.CustomerName,
                    project.SalesOrderNumber,
                    project.DeletedAt!.Value,
                    project.DeletedByDisplayName))
                .ToList();
        });

        api.MapPost("/archived-projects/{id:int}/restore", async (
            int id,
            ProjectActionDto dto,
            ProjectTrackerDbContext db,
            ProjectAuditService audit,
            CancellationToken cancellationToken) =>
        {
            var project = await db.Projects
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(project => project.Id == id && project.DeletedAt != null, cancellationToken);
            if (project is null)
            {
                return Results.NotFound();
            }
            if (dto.Version != project.Version)
            {
                return Results.Conflict(new ConcurrencyConflictDto(
                    "ConcurrencyConflict",
                    "This archived project changed before it could be restored. Reload the archived-project list and try again.",
                    "Project",
                    project.Id));
            }

            var archivedAt = project.DeletedAt;
            project.DeletedAt = null;
            project.DeletedByAccountName = null;
            project.DeletedByDisplayName = null;
            if (project.Status != ProjectStatus.Complete)
            {
                var lastPriority = await db.Projects
                    .Where(candidate => candidate.Status != ProjectStatus.Complete)
                    .Select(candidate => candidate.PriorityRank)
                    .MaxAsync(cancellationToken) ?? 0;
                project.PriorityRank = lastPriority + 1;
            }
            project.Version++;
            project.UpdatedAt = DateTimeOffset.UtcNow;
            audit.Record(
                db,
                project,
                "ProjectRestored",
                "Restored archived project",
                [new ProjectAuditChange("Archived at", archivedAt?.ToString("O"), null)]);
            await db.SaveChangesAsync(cancellationToken);
            return Results.NoContent();
        }).RequireAuthorization("RestoreArchived");

        api.MapDelete("/archived-projects/{id:int}", PermanentlyDeleteAsync)
            .RequireAuthorization(PermanentDeletePolicyName);

        return api;
    }

    public static async Task<IResult> PermanentlyDeleteAsync(
        int id,
        [FromBody] ArchivedProjectPermanentDeleteDto dto,
        ProjectTrackerDbContext db,
        ControlledImportReviewStore importReviews,
        CancellationToken cancellationToken)
    {
        var project = await db.Projects
            .IgnoreQueryFilters()
            .AsNoTracking()
            .FirstOrDefaultAsync(
                candidate => candidate.Id == id && candidate.DeletedAt != null,
                cancellationToken);
        if (project is null)
        {
            return Results.NotFound();
        }
        if (dto.Version != project.Version)
        {
            return Results.Conflict(new ConcurrencyConflictDto(
                "ConcurrencyConflict",
                "This archived project changed before it could be permanently deleted. Reload the archived-project list and try again.",
                "Project",
                project.Id));
        }
        if (!string.Equals(dto.Confirmation, project.ProgramName, StringComparison.Ordinal))
        {
            return Results.BadRequest("Enter the exact project name to confirm permanent deletion.");
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        await db.UserNotifications
            .IgnoreQueryFilters()
            .Where(notification => notification.ProjectId == id)
            .ExecuteDeleteAsync(cancellationToken);
        await db.StatusHistory
            .IgnoreQueryFilters()
            .Where(history => history.ProjectId == id)
            .ExecuteDeleteAsync(cancellationToken);
        await db.ProjectAuditEntries
            .IgnoreQueryFilters()
            .Where(entry => entry.ProjectId == id)
            .ExecuteDeleteAsync(cancellationToken);
        await db.ProjectMessages
            .IgnoreQueryFilters()
            .Where(message => message.ProjectId == id)
            .ExecuteDeleteAsync(cancellationToken);
        await db.TaskOvertimeDays
            .IgnoreQueryFilters()
            .Where(day => day.ProjectTask.ProjectId == id)
            .ExecuteDeleteAsync(cancellationToken);
        await db.Tasks
            .IgnoreQueryFilters()
            .Where(task => task.ProjectId == id && task.DependencyTaskId != null)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(task => task.DependencyTaskId, (int?)null),
                cancellationToken);
        await db.Tasks
            .IgnoreQueryFilters()
            .Where(task => task.ProjectId == id)
            .ExecuteDeleteAsync(cancellationToken);

        var deletedProjects = await db.Projects
            .IgnoreQueryFilters()
            .Where(candidate =>
                candidate.Id == id
                && candidate.DeletedAt != null
                && candidate.Version == dto.Version
                && candidate.ProgramName == dto.Confirmation)
            .ExecuteDeleteAsync(cancellationToken);
        if (deletedProjects != 1)
        {
            await transaction.RollbackAsync(cancellationToken);
            return Results.Conflict(new ConcurrencyConflictDto(
                "ConcurrencyConflict",
                "This archived project changed before it could be permanently deleted. Reload the archived-project list and try again.",
                "Project",
                id));
        }

        await transaction.CommitAsync(cancellationToken);
        importReviews.RemoveForProject(id);
        return Results.NoContent();
    }
}
