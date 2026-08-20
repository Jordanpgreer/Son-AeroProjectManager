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
                includeScheduleConfirmations: currentUser.HasPermission(ProjectTracker.Api.Auth.ProjectTrackerPermissions.OperationScheduleConfirm),
                cancellationToken: cancellationToken);

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
                includeScheduleConfirmations: currentUser.HasPermission(ProjectTracker.Api.Auth.ProjectTrackerPermissions.OperationScheduleConfirm),
                cancellationToken: cancellationToken);
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
                includeScheduleConfirmations: currentUser.HasPermission(ProjectTracker.Api.Auth.ProjectTrackerPermissions.OperationScheduleConfirm),
                cancellationToken: cancellationToken);
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
                includeScheduleConfirmations: currentUser.HasPermission(ProjectTracker.Api.Auth.ProjectTrackerPermissions.OperationScheduleConfirm),
                cancellationToken: cancellationToken);
            return Results.NoContent();
        });

        api.MapPost("/notifications/{id:int}/confirm", async (
            int id,
            HttpRequest request,
            ProjectTrackerDbContext db,
            CurrentUserService currentUser,
            OperationScheduleReminderService reminders,
            CancellationToken cancellationToken) =>
        {
            // Requiring JSON makes this a CORS-preflighted browser mutation. An
            // untrusted site cannot submit it with a simple cross-site form POST.
            if (!HasMutationJsonContentType(request))
            {
                return Results.Problem(
                    statusCode: StatusCodes.Status415UnsupportedMediaType,
                    title: "application/json is required.");
            }

            if (currentUser.IsAccessPreview)
            {
                return Results.Forbid();
            }

            var userId = await CurrentUserIdAsync(db, currentUser, cancellationToken);
            if (userId is null)
            {
                return Results.Forbid();
            }

            var result = await reminders.ConfirmAsync(
                id,
                userId.Value,
                currentUser.HasPermission(ProjectTracker.Api.Auth.ProjectTrackerPermissions.OperationScheduleConfirm),
                currentUser.AccountName,
                currentUser.DisplayName,
                DateOnly.FromDateTime(DateTime.Today),
                cancellationToken);
            return result.Status switch
            {
                OperationScheduleConfirmationStatus.Confirmed => Results.NoContent(),
                OperationScheduleConfirmationStatus.AlreadyConfirmed => Results.NoContent(),
                OperationScheduleConfirmationStatus.Forbidden => Results.Forbid(),
                OperationScheduleConfirmationStatus.NotFound => Results.NotFound(),
                _ => Results.Conflict("This reminder no longer matches the operation schedule. Refresh notifications and try again.")
            };
        });

        api.MapPost("/notifications/{id:int}/respond", async (
            int id,
            OperationScheduleResponseDto dto,
            HttpRequest request,
            ProjectTrackerDbContext db,
            CurrentUserService currentUser,
            OperationScheduleReminderService reminders,
            CancellationToken cancellationToken) =>
        {
            if (!HasMutationJsonContentType(request))
            {
                return Results.Problem(
                    statusCode: StatusCodes.Status415UnsupportedMediaType,
                    title: "application/json is required.");
            }

            if (currentUser.IsAccessPreview)
            {
                return Results.Forbid();
            }

            var userId = await CurrentUserIdAsync(db, currentUser, cancellationToken);
            if (userId is null)
            {
                return Results.Forbid();
            }

            var response = string.Equals(dto.Response, nameof(OperationScheduleResponse.Yes), StringComparison.OrdinalIgnoreCase)
                ? OperationScheduleResponse.Yes
                : string.Equals(dto.Response, nameof(OperationScheduleResponse.No), StringComparison.OrdinalIgnoreCase)
                    ? OperationScheduleResponse.No
                    : (OperationScheduleResponse?)null;
            if (response is null)
            {
                return Results.BadRequest(new { message = "Response must be Yes or No." });
            }

            var result = await reminders.RespondAsync(
                id,
                userId.Value,
                currentUser.HasPermission(ProjectTracker.Api.Auth.ProjectTrackerPermissions.OperationScheduleConfirm),
                currentUser.AccountName,
                currentUser.DisplayName,
                response.Value,
                DateOnly.FromDateTime(DateTime.Today),
                cancellationToken);
            return result.Status switch
            {
                OperationScheduleConfirmationStatus.Confirmed => Results.NoContent(),
                OperationScheduleConfirmationStatus.AlreadyConfirmed => Results.NoContent(),
                OperationScheduleConfirmationStatus.Forbidden => Results.Forbid(),
                OperationScheduleConfirmationStatus.NotFound => Results.NotFound(),
                _ => Results.Conflict("This reminder no longer matches the operation schedule. Refresh notifications and try again.")
            };
        });

        api.MapPost("/notifications/{id:int}/snooze", async (
            int id,
            HttpRequest request,
            ProjectTrackerDbContext db,
            CurrentUserService currentUser,
            OperationScheduleReminderService reminders,
            CancellationToken cancellationToken) =>
        {
            if (!HasMutationJsonContentType(request))
            {
                return Results.Problem(
                    statusCode: StatusCodes.Status415UnsupportedMediaType,
                    title: "application/json is required.");
            }

            if (currentUser.IsAccessPreview)
            {
                return Results.Forbid();
            }

            var userId = await CurrentUserIdAsync(db, currentUser, cancellationToken);
            if (userId is null)
            {
                return Results.Forbid();
            }

            var result = await reminders.SnoozeAsync(
                id,
                userId.Value,
                currentUser.HasPermission(ProjectTracker.Api.Auth.ProjectTrackerPermissions.OperationScheduleConfirm),
                DateOnly.FromDateTime(DateTime.Today),
                cancellationToken);
            return result.Status switch
            {
                OperationScheduleConfirmationStatus.Snoozed => Results.NoContent(),
                OperationScheduleConfirmationStatus.AlreadyConfirmed => Results.NoContent(),
                OperationScheduleConfirmationStatus.Forbidden => Results.Forbid(),
                OperationScheduleConfirmationStatus.NotFound => Results.NotFound(),
                _ => Results.Conflict("This reminder no longer matches the operation schedule. Refresh notifications and try again.")
            };
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

    private static bool HasMutationJsonContentType(HttpRequest request) =>
        request.HasJsonContentType();

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
