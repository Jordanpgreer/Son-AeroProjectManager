using Microsoft.EntityFrameworkCore;
using ProjectTracker.Api.Data;
using ProjectTracker.Api.Dtos;
using ProjectTracker.Api.Services;
using SonAero.Platform.Security;

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
                if (currentUser.IsAccessPreview) return Results.Ok(Array.Empty<UserNotificationDto>());
                return Results.Forbid();
            }

            var limit = Math.Clamp(take ?? 50, 1, 100);
            var notifications = await notificationReader.GetAsync(
                userId.Value,
                currentUser.EffectiveAccountName!,
                unreadOnly == true,
                limit,
                cancellationToken);

            return Results.Ok(notifications);
        });

        api.MapGet("/notifications/unread-count", async (
            ProjectTrackerDbContext db,
            CurrentUserService currentUser,
            NotificationReadService notificationReader,
            CancellationToken cancellationToken) =>
        {
            var userId = await CurrentUserIdAsync(db, currentUser, cancellationToken);
            if (userId is null)
            {
                if (currentUser.IsAccessPreview) return Results.Ok(new NotificationCountDto(0));
                return Results.Forbid();
            }

            var count = await notificationReader.GetUnreadCountAsync(
                userId.Value,
                currentUser.EffectiveAccountName!,
                cancellationToken);
            return Results.Ok(new NotificationCountDto(count));
        });

        api.MapPost("/notifications/{id:int}/read", async (
            int id,
            ProjectTrackerDbContext db,
            CurrentUserService currentUser,
            NotificationReadService notificationReader,
            CancellationToken cancellationToken) =>
        {
            if (currentUser.IsAccessPreview)
            {
                return Results.Forbid();
            }

            var userId = await CurrentUserIdAsync(db, currentUser, cancellationToken);
            if (userId is null)
            {
                return Results.Forbid();
            }

            var found = await notificationReader.MarkReadAsync(
                id,
                userId.Value,
                currentUser.AccountName,
                cancellationToken);
            if (!found)
            {
                return Results.NotFound();
            }
            return Results.NoContent();
        });

        api.MapPost("/notifications/read-all", async (
            ProjectTrackerDbContext db,
            CurrentUserService currentUser,
            NotificationReadService notificationReader,
            CancellationToken cancellationToken) =>
        {
            if (currentUser.IsAccessPreview)
            {
                return Results.Forbid();
            }

            var userId = await CurrentUserIdAsync(db, currentUser, cancellationToken);
            if (userId is null)
            {
                return Results.Forbid();
            }

            await notificationReader.MarkAllReadAsync(
                userId.Value,
                currentUser.AccountName,
                cancellationToken);
            return Results.NoContent();
        });

        api.MapDelete("/notifications/{id:int}", async (
            int id,
            ProjectTrackerDbContext db,
            CurrentUserService currentUser,
            NotificationReadService notificationReader,
            CancellationToken cancellationToken) =>
        {
            if (currentUser.IsAccessPreview)
            {
                return Results.Forbid();
            }

            var userId = await CurrentUserIdAsync(db, currentUser, cancellationToken);
            if (userId is null)
            {
                return Results.Forbid();
            }

            var deleted = await notificationReader.DeleteAsync(id, userId.Value, cancellationToken);
            if (!deleted)
            {
                return Results.NotFound();
            }

            return Results.NoContent();
        });

        api.MapDelete("/notifications", async (
            ProjectTrackerDbContext db,
            CurrentUserService currentUser,
            NotificationReadService notificationReader,
            CancellationToken cancellationToken) =>
        {
            if (currentUser.IsAccessPreview)
            {
                return Results.Forbid();
            }

            var userId = await CurrentUserIdAsync(db, currentUser, cancellationToken);
            if (userId is null)
            {
                return Results.Forbid();
            }

            await notificationReader.DeleteAllAsync(userId.Value, cancellationToken);
            return Results.NoContent();
        });

        return api;
    }

    private static Task<int?> CurrentUserIdAsync(
        ProjectTrackerDbContext db,
        CurrentUserService currentUser,
        CancellationToken cancellationToken)
    {
        var lookupKeys = WindowsAccountNames.LookupKeys(currentUser.EffectiveAccountName);
        return db.Users
            .Where(user => user.IsActive && lookupKeys.Contains(user.AccountName.ToUpper()))
            .Select(user => (int?)user.Id)
            .FirstOrDefaultAsync(cancellationToken);
    }
}
