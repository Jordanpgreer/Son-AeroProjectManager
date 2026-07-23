using Microsoft.EntityFrameworkCore;
using ProjectTracker.Api.Data;
using ProjectTracker.Api.Dtos;

namespace ProjectTracker.Api.Services;

public sealed class NotificationReadService(ProjectTrackerDbContext db)
{
    public async Task<IReadOnlyList<UserNotificationDto>> GetAsync(
        int recipientUserId,
        bool unreadOnly,
        int take,
        CancellationToken cancellationToken = default)
    {
        var query = db.UserNotifications
            .AsNoTracking()
            .Where(notification => notification.RecipientUserId == recipientUserId);

        if (unreadOnly)
        {
            query = query.Where(notification => notification.ReadAt == null);
        }

        var projection = query.Select(notification => new UserNotificationDto(
            notification.Id,
            notification.Kind,
            notification.ProjectId,
            notification.Project.ProgramName,
            notification.ProjectTaskId,
            notification.ProjectTask == null ? null : notification.ProjectTask.Title,
            notification.ActorAccountName,
            notification.ActorDisplayName,
            notification.Title,
            notification.BodyPreview,
            notification.CreatedAt,
            notification.ReadAt));

        // SQLite stores DateTimeOffset as text and cannot translate ORDER BY for it.
        // Keep SQL Server paging server-side while preserving exact ordering locally.
        if (db.Database.IsSqlite())
        {
            var candidates = await projection.ToListAsync(cancellationToken);
            return candidates
                .OrderByDescending(notification => notification.CreatedAt)
                .ThenByDescending(notification => notification.Id)
                .Take(take)
                .ToList();
        }

        return await projection
            .OrderByDescending(notification => notification.CreatedAt)
            .ThenByDescending(notification => notification.Id)
            .Take(take)
            .ToListAsync(cancellationToken);
    }
}
