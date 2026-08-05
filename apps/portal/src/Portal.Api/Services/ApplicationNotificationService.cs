using Microsoft.EntityFrameworkCore;
using Portal.Api.Data;
using Portal.Api.Dtos;
using SonAero.Platform.Security;

namespace Portal.Api.Services;

public sealed class ApplicationNotificationService(
    PortalRoleDbContext db,
    ILogger<ApplicationNotificationService> logger)
{
    private const string ProjectTrackerApplicationId = "project-tracker";
    private const string ProjectChatMention = "ProjectChatMention";
    private const string OperationNoteMention = "OperationNoteMention";

    public async Task<IReadOnlyList<ApplicationNotificationDto>> GetUnreadCountsAsync(
        string accountName,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var lookupKeys = WindowsAccountNames.LookupKeys(accountName);
            var user = await db.Users
                .AsNoTracking()
                .Where(user => user.IsActive && lookupKeys.Contains(user.AccountName.ToUpper()))
                .Select(user => new { user.Id, user.AccountName })
                .FirstOrDefaultAsync(cancellationToken);

            if (user is null)
            {
                return [];
            }

            var selfLookupKeys = WindowsAccountNames.LookupKeys(user.AccountName);
            var unreadCount = await db.UserNotifications
                .AsNoTracking()
                .CountAsync(
                    notification =>
                        notification.RecipientUserId == user.Id
                        && notification.ReadAt == null
                        && !selfLookupKeys.Contains(notification.ActorAccountName.ToUpper())
                        && ((notification.Kind == ProjectChatMention && notification.ProjectMessageId != null)
                            || (notification.Kind == OperationNoteMention && notification.ProjectTaskId != null)),
                    cancellationToken);

            return unreadCount == 0
                ? []
                : [new ApplicationNotificationDto(ProjectTrackerApplicationId, unreadCount)];
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(
                exception,
                "Application notification counts are unavailable; the hub will continue without badges.");
            return [];
        }
    }
}
