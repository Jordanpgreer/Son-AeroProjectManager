using System.Data.Common;
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

    public async Task<IReadOnlyList<ApplicationNotificationDto>> GetUnreadCountsAsync(
        string accountName,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var lookupKeys = WindowsAccountNames.LookupKeys(accountName);
            var userId = await db.Users
                .AsNoTracking()
                .Where(user => user.IsActive && lookupKeys.Contains(user.AccountName.ToUpper()))
                .Select(user => (int?)user.Id)
                .FirstOrDefaultAsync(cancellationToken);

            if (userId is null)
            {
                return [];
            }

            var unreadCount = await db.UserNotifications
                .AsNoTracking()
                .CountAsync(
                    notification => notification.RecipientUserId == userId && notification.ReadAt == null,
                    cancellationToken);

            return unreadCount == 0
                ? []
                : [new ApplicationNotificationDto(ProjectTrackerApplicationId, unreadCount)];
        }
        catch (Exception exception) when (exception is DbException or InvalidOperationException)
        {
            logger.LogWarning(
                exception,
                "Application notification counts are unavailable; the hub will continue without badges.");
            return [];
        }
    }
}
