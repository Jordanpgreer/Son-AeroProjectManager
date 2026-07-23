using Microsoft.EntityFrameworkCore;
using ProjectTracker.Api.Data;
using ProjectTracker.Api.Dtos;
using ProjectTracker.Api.Services;

namespace ProjectTracker.Api.Endpoints;

public static class NotificationEndpoints
{
    public static RouteGroupBuilder MapNotificationEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/notifications", async (
            bool? unreadOnly,
            int? take,
            ProjectTrackerDbContext db,
            CurrentUserService currentUser,
            NotificationReadService notificationReader,
            CancellationToken cancellationToken) =>
        {
            var userId = await CurrentUserIdAsync(db, currentUser, cancellationToken);
            if (userId is null)
            {
                return Results.Forbid();
            }

            var limit = Math.Clamp(take ?? 50, 1, 100);
            var notifications = await notificationReader.GetAsync(
                userId.Value,
                unreadOnly == true,
                limit,
                cancellationToken);

            return Results.Ok(notifications);
        });

        api.MapGet("/notifications/unread-count", async (
            ProjectTrackerDbContext db,
            CurrentUserService currentUser,
            CancellationToken cancellationToken) =>
        {
            var userId = await CurrentUserIdAsync(db, currentUser, cancellationToken);
            if (userId is null)
            {
                return Results.Forbid();
            }

            var count = await db.UserNotifications.CountAsync(
                notification => notification.RecipientUserId == userId.Value && notification.ReadAt == null,
                cancellationToken);
            return Results.Ok(new NotificationCountDto(count));
        });

        api.MapPost("/notifications/{id:int}/read", async (
            int id,
            ProjectTrackerDbContext db,
            CurrentUserService currentUser,
            CancellationToken cancellationToken) =>
        {
            var userId = await CurrentUserIdAsync(db, currentUser, cancellationToken);
            if (userId is null)
            {
                return Results.Forbid();
            }

            var notification = await db.UserNotifications.FirstOrDefaultAsync(
                candidate => candidate.Id == id && candidate.RecipientUserId == userId.Value,
                cancellationToken);
            if (notification is null)
            {
                return Results.NotFound();
            }

            notification.ReadAt ??= DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(cancellationToken);
            return Results.NoContent();
        });

        api.MapPost("/notifications/read-all", async (
            ProjectTrackerDbContext db,
            CurrentUserService currentUser,
            CancellationToken cancellationToken) =>
        {
            var userId = await CurrentUserIdAsync(db, currentUser, cancellationToken);
            if (userId is null)
            {
                return Results.Forbid();
            }

            var unread = await db.UserNotifications
                .Where(notification => notification.RecipientUserId == userId.Value && notification.ReadAt == null)
                .ToListAsync(cancellationToken);
            var readAt = DateTimeOffset.UtcNow;
            foreach (var notification in unread)
            {
                notification.ReadAt = readAt;
            }

            await db.SaveChangesAsync(cancellationToken);
            return Results.NoContent();
        });

        return api;
    }

    private static Task<int?> CurrentUserIdAsync(
        ProjectTrackerDbContext db,
        CurrentUserService currentUser,
        CancellationToken cancellationToken)
    {
        var normalizedAccount = currentUser.AccountName.ToUpper();
        return db.Users
            .Where(user => user.IsActive && user.AccountName.ToUpper() == normalizedAccount)
            .Select(user => (int?)user.Id)
            .FirstOrDefaultAsync(cancellationToken);
    }
}
