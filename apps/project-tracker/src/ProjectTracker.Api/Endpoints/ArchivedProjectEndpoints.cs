using Microsoft.EntityFrameworkCore;
using ProjectTracker.Api.Data;
using ProjectTracker.Api.Dtos;
using ProjectTracker.Api.Models;
using ProjectTracker.Api.Services;

namespace ProjectTracker.Api.Endpoints;

public static class ArchivedProjectEndpoints
{
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

        return api;
    }
}
