using System.Data;
using Microsoft.EntityFrameworkCore;
using Portal.Api.Data;
using SonAero.Platform.Notifications;
using SonAero.Platform.Security;

namespace Portal.Api.Services;

public sealed class ArdaPushForegroundService(
    PortalRoleDbContext db,
    ArdaPresenceTracker presence)
{
    public static readonly TimeSpan ClaimLifetime = TimeSpan.FromSeconds(30);

    public async Task<IReadOnlyList<ArdaPushForegroundNotification>?> ClaimAsync(
        string accountName,
        ArdaPushForegroundRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!ValidClient(request.ModuleId, request.ClientId)
            || !presence.IsVisibleClient(accountName, request.ModuleId!, request.ClientId!))
            return null;

        var lookupKeys = WindowsAccountNames.LookupKeys(accountName);
        var now = DateTimeOffset.UtcNow;
        var claimedBy = ClaimKey(accountName, request.ModuleId!, request.ClientId!);
        await using var transaction = await db.Database.BeginTransactionAsync(
            IsolationLevel.Serializable, cancellationToken);
        var candidates = await db.ArdaPushNotifications
            .Where(notification => notification.DeliveredAt == null
                && lookupKeys.Contains(notification.RecipientAccountName.ToUpper()))
            .OrderBy(notification => notification.Id)
            .Take(250)
            .ToListAsync(cancellationToken);
        // SQLite stores DateTimeOffset values as text and cannot translate ordering comparisons;
        // keep the bounded expiry filter provider-neutral. Production SQL Server uses the same result.
        var notifications = candidates
            .Where(notification => notification.ForegroundClaimExpiresAt is null
                || notification.ForegroundClaimExpiresAt <= now
                || notification.ForegroundClaimedBy == claimedBy)
            .Take(10)
            .ToList();

        foreach (var notification in notifications)
        {
            notification.ForegroundClaimedBy = claimedBy;
            notification.ForegroundClaimExpiresAt = now.Add(ClaimLifetime);
        }
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return notifications.Select(notification => new ArdaPushForegroundNotification(
            notification.Id,
            notification.SourceModule,
            notification.Title,
            notification.Body,
            notification.TargetUrl,
            notification.CreatedAt)).ToList();
    }

    public async Task<bool> AcknowledgeAsync(
        long notificationId,
        string accountName,
        ArdaPushForegroundRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!ValidClient(request.ModuleId, request.ClientId)
            || !presence.IsVisibleClient(accountName, request.ModuleId!, request.ClientId!))
            return false;

        var lookupKeys = WindowsAccountNames.LookupKeys(accountName);
        var claimedBy = ClaimKey(accountName, request.ModuleId!, request.ClientId!);
        var notification = await db.ArdaPushNotifications.SingleOrDefaultAsync(candidate =>
            candidate.Id == notificationId
            && candidate.DeliveredAt == null
            && lookupKeys.Contains(candidate.RecipientAccountName.ToUpper())
            && candidate.ForegroundClaimedBy == claimedBy,
            cancellationToken);
        if (notification is null) return false;

        notification.DeliveredAt = DateTimeOffset.UtcNow;
        notification.DeliveryMethod = "InApp";
        notification.ForegroundClaimExpiresAt = null;
        notification.LastError = null;
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    private static bool ValidClient(string? moduleId, string? clientId) =>
        !string.IsNullOrWhiteSpace(moduleId) && moduleId.Length <= 64
        && !string.IsNullOrWhiteSpace(clientId) && clientId.Length <= 128;

    private static string ClaimKey(string accountName, string moduleId, string clientId) =>
        $"{WindowsAccountNames.Normalize(accountName)}|{moduleId.Trim()}|{clientId.Trim()}";
}
