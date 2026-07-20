using Microsoft.EntityFrameworkCore;
using ProjectTracker.Api.Data;
using ProjectTracker.Api.Models;

namespace ProjectTracker.Api.Services;

public sealed record RecentProjectNote(ProjectTask Task, DateTimeOffset UpdatedAt);

public static class ProjectNoteService
{
    public static RecentProjectNote? GetMostRecent(Project project)
    {
        var task = project.Tasks
            .Where(candidate => !string.IsNullOrWhiteSpace(candidate.Notes))
            .OrderByDescending(candidate => candidate.NoteUpdatedAt ?? candidate.CreatedAt)
            .FirstOrDefault();

        return task is null
            ? null
            : new RecentProjectNote(task, task.NoteUpdatedAt ?? task.CreatedAt);
    }

    public static async Task BackfillUpdatedAtAsync(ProjectTrackerDbContext db, CancellationToken cancellationToken = default)
    {
        var tasks = await db.Tasks
            .Where(task => task.Notes != null && task.Notes.Trim() != string.Empty)
            .ToListAsync(cancellationToken);
        if (tasks.Count == 0)
        {
            return;
        }

        var taskIds = tasks.Select(task => task.Id).ToHashSet();
        var audits = await db.ProjectAuditEntries
            .Where(entry => entry.ProjectTaskId != null && taskIds.Contains(entry.ProjectTaskId.Value))
            .ToListAsync(cancellationToken);

        foreach (var task in tasks)
        {
            var auditedUpdate = audits
                .Where(entry => entry.ProjectTaskId == task.Id)
                .Where(entry => ProjectAuditService.ReadChanges(entry.ChangesJson)
                    .Any(change => change.Field == "Notes" && !string.IsNullOrWhiteSpace(change.NewValue)))
                .OrderByDescending(entry => entry.ChangedAt)
                .Select(entry => (DateTimeOffset?)entry.ChangedAt)
                .FirstOrDefault();

            if (auditedUpdate is not null)
            {
                task.NoteUpdatedAt = auditedUpdate;
            }
            else if (task.NoteUpdatedAt is null || task.NoteUpdatedAt == task.UpdatedAt)
            {
                task.NoteUpdatedAt = task.CreatedAt;
            }
        }
    }
}
