using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Portal.Api.Configuration;
using Portal.Api.Data;
using SonAero.Platform.Security;
using WebPush;

namespace Portal.Api.Services;

public enum ArdaPushSendStatus
{
    Delivered,
    Stale,
    Failed
}

public interface IArdaWebPushSender
{
    Task<ArdaPushSendStatus> SendAsync(
        ArdaPushSubscriptionRecord subscription,
        string payload,
        WebPushOptions options,
        CancellationToken cancellationToken);
}

public sealed class ArdaWebPushSender : IArdaWebPushSender, IDisposable
{
    private readonly WebPushClient client = new();

    public async Task<ArdaPushSendStatus> SendAsync(
        ArdaPushSubscriptionRecord subscription,
        string payload,
        WebPushOptions options,
        CancellationToken cancellationToken)
    {
        try
        {
            var browserSubscription = new PushSubscription(
                subscription.Endpoint,
                subscription.P256dh,
                subscription.Auth);
            var vapid = new VapidDetails(options.Subject, options.PublicKey, options.PrivateKey);
            await client.SendNotificationAsync(browserSubscription, payload, vapid, cancellationToken);
            return ArdaPushSendStatus.Delivered;
        }
        catch (WebPushException exception) when (
            exception.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
        {
            return ArdaPushSendStatus.Stale;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return ArdaPushSendStatus.Failed;
        }
    }

    public void Dispose() => client.Dispose();
}

public sealed class ArdaPushDeliveryWorker(
    IServiceScopeFactory scopeFactory,
    IArdaWebPushSender sender,
    IOptions<WebPushOptions> options,
    ArdaPresenceTracker presence,
    ILogger<ArdaPushDeliveryWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (options.Value.IsConfigured)
                    await DeliverPendingAsync(stoppingToken);
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Arda Web Push delivery sweep failed.");
                await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);
            }
        }
    }

    public async Task DeliverPendingAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PortalRoleDbContext>();
        var now = DateTimeOffset.UtcNow;
        List<long> ids;
        if ((db.Database.ProviderName ?? string.Empty).Contains("Sqlite", StringComparison.OrdinalIgnoreCase))
        {
            var candidates = await db.ArdaPushNotifications
                .Where(notification => notification.DeliveredAt == null)
                .OrderBy(notification => notification.Id)
                .ToListAsync(cancellationToken);
            ids = candidates
                .Where(notification => notification.NextAttemptAt <= now
                    && (notification.ForegroundClaimExpiresAt is null
                        || notification.ForegroundClaimExpiresAt <= now))
                .Select(notification => notification.Id)
                .Take(25)
                .ToList();
        }
        else
        {
            ids = await db.ArdaPushNotifications
                .Where(notification => notification.DeliveredAt == null
                    && notification.NextAttemptAt <= now
                    && (notification.ForegroundClaimExpiresAt == null
                        || notification.ForegroundClaimExpiresAt <= now))
                .OrderBy(notification => notification.NextAttemptAt)
                .ThenBy(notification => notification.Id)
                .Select(notification => notification.Id)
                .Take(25)
                .ToListAsync(cancellationToken);
        }
        foreach (var id in ids) await DeliverAsync(id, cancellationToken);
    }

    public async Task DeliverAsync(long notificationId, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PortalRoleDbContext>();
        var notification = await db.ArdaPushNotifications.SingleOrDefaultAsync(
            candidate => candidate.Id == notificationId, cancellationToken);
        if (notification is null || notification.DeliveredAt is not null) return;

        var now = DateTimeOffset.UtcNow;
        if (notification.ForegroundClaimExpiresAt > now) return;
        if (presence.HasVisibleClient(notification.RecipientAccountName))
        {
            notification.NextAttemptAt = now.AddSeconds(20);
            await db.SaveChangesAsync(cancellationToken);
            return;
        }

        var lookupKeys = WindowsAccountNames.LookupKeys(notification.RecipientAccountName);
        var userId = await db.Users
            .Where(user => user.IsActive && lookupKeys.Contains(user.AccountName.ToUpper()))
            .Select(user => (int?)user.Id)
            .FirstOrDefaultAsync(cancellationToken);
        if (userId is null)
        {
            ScheduleRetry(notification, now, "The recipient does not have an active Arda account.");
            await db.SaveChangesAsync(cancellationToken);
            return;
        }

        var subscriptions = await db.ArdaPushSubscriptions
            .Where(subscription => subscription.AppUserId == userId.Value)
            .ToListAsync(cancellationToken);
        if (subscriptions.Count == 0)
        {
            ScheduleRetry(notification, now, "The recipient has no Portal push subscription.");
            await db.SaveChangesAsync(cancellationToken);
            return;
        }

        var staleIds = new List<long>();
        var delivered = false;
        foreach (var subscription in subscriptions)
        {
            if (subscription.ExpirationTime is { } expiration && expiration <= now)
            {
                staleIds.Add(subscription.Id);
                continue;
            }

            var result = await sender.SendAsync(
                subscription,
                CreatePayload(notification),
                options.Value,
                cancellationToken);
            delivered |= result == ArdaPushSendStatus.Delivered;
            if (result == ArdaPushSendStatus.Stale) staleIds.Add(subscription.Id);
        }

        if (staleIds.Count > 0)
            await db.ArdaPushSubscriptions
                .Where(subscription => staleIds.Contains(subscription.Id))
                .ExecuteDeleteAsync(cancellationToken);

        notification.AttemptCount++;
        if (delivered)
        {
            notification.DeliveredAt = now;
            notification.DeliveryMethod = "WebPush";
            notification.LastError = null;
        }
        else
        {
            ScheduleRetry(notification, now, "No push service accepted the notification.", incrementAttempt: false);
        }
        await db.SaveChangesAsync(cancellationToken);
    }

    public static string CreatePayload(ArdaPushNotificationRecord notification)
    {
        var targetUrl = $"/api/push/open/{notification.Id}";
        return JsonSerializer.Serialize(new
        {
            title = notification.Title,
            body = notification.Body,
            targetUrl,
            tag = $"arda-{notification.SourceModule}-{notification.SourceNotificationKey}",
            icon = "/brand/arda-mark.png",
            badge = "/brand/arda-mark.png",
            data = new
            {
                targetUrl,
                notificationId = notification.Id,
                sourceModule = notification.SourceModule
            }
        });
    }

    private static void ScheduleRetry(
        ArdaPushNotificationRecord notification,
        DateTimeOffset now,
        string error,
        bool incrementAttempt = true)
    {
        if (incrementAttempt) notification.AttemptCount++;
        var minutes = Math.Min(60, Math.Max(1, Math.Pow(2, Math.Min(notification.AttemptCount, 6))));
        notification.NextAttemptAt = now.AddMinutes(minutes);
        notification.LastError = error;
    }
}
