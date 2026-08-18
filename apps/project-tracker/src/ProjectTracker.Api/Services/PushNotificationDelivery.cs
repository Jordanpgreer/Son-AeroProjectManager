using System.Net;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ProjectTracker.Api.Configuration;
using ProjectTracker.Api.Data;
using ProjectTracker.Api.Models;
using WebPush;

namespace ProjectTracker.Api.Services;

public interface IPushNotificationQueue
{
    bool TryEnqueue(int notificationId);
    IAsyncEnumerable<int> ReadAllAsync(CancellationToken cancellationToken);
}

public sealed class PushNotificationQueue : IPushNotificationQueue
{
    private readonly Channel<int> channel = Channel.CreateBounded<int>(new BoundedChannelOptions(1024)
    {
        FullMode = BoundedChannelFullMode.DropOldest,
        SingleReader = true,
        SingleWriter = false
    });

    public bool TryEnqueue(int notificationId) => notificationId > 0 && channel.Writer.TryWrite(notificationId);

    public IAsyncEnumerable<int> ReadAllAsync(CancellationToken cancellationToken) =>
        channel.Reader.ReadAllAsync(cancellationToken);
}

public enum PushSendStatus
{
    Delivered,
    Stale,
    Failed
}

public interface IWebPushSender
{
    Task<PushSendStatus> SendAsync(
        PushSubscriptionRecord subscription,
        string payload,
        WebPushOptions options,
        CancellationToken cancellationToken);
}

public sealed class WebPushSender : IWebPushSender, IDisposable
{
    private readonly WebPushClient client = new();

    public async Task<PushSendStatus> SendAsync(
        PushSubscriptionRecord subscription,
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
            return PushSendStatus.Delivered;
        }
        catch (WebPushException exception) when (
            exception.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
        {
            return PushSendStatus.Stale;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return PushSendStatus.Failed;
        }
    }

    public void Dispose() => client.Dispose();
}

public sealed class PushNotificationWorker(
    IPushNotificationQueue queue,
    IServiceScopeFactory scopeFactory,
    IWebPushSender sender,
    IOptions<WebPushOptions> options,
    ILogger<PushNotificationWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var notificationId in queue.ReadAllAsync(stoppingToken))
        {
            if (!options.Value.IsConfigured) continue;
            try
            {
                await DeliverAsync(notificationId, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Web Push delivery failed for notification {NotificationId}.", notificationId);
            }
        }
    }

    public async Task DeliverAsync(int notificationId, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ProjectTrackerDbContext>();
        var notification = await db.UserNotifications
            .IgnoreQueryFilters()
            .AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.Id == notificationId, cancellationToken);
        if (notification is null) return;

        var subscriptions = await db.PushSubscriptions
            .Where(subscription => subscription.AppUserId == notification.RecipientUserId)
            .ToListAsync(cancellationToken);
        if (subscriptions.Count == 0) return;

        var payload = CreatePayload(notification);
        var staleIds = new List<int>();
        foreach (var subscription in subscriptions)
        {
            if (subscription.ExpirationTime is { } expiration && expiration <= DateTimeOffset.UtcNow)
            {
                staleIds.Add(subscription.Id);
                continue;
            }

            var status = await sender.SendAsync(subscription, payload, options.Value, cancellationToken);
            if (status == PushSendStatus.Stale) staleIds.Add(subscription.Id);
            if (status == PushSendStatus.Failed)
            {
                logger.LogInformation(
                    "Web Push delivery was not accepted for notification {NotificationId} and subscription {SubscriptionId}.",
                    notification.Id,
                    subscription.Id);
            }
        }

        if (staleIds.Count > 0)
        {
            await db.PushSubscriptions
                .Where(subscription => staleIds.Contains(subscription.Id))
                .ExecuteDeleteAsync(cancellationToken);
        }
    }

    public static string CreatePayload(UserNotification notification)
    {
        var targetUrl = $"/?notificationProjectId={notification.ProjectId}"
            + $"&notificationKind={Uri.EscapeDataString(notification.Kind.ToString())}"
            + $"&notificationId={notification.Id}";
        if (notification.ProjectTaskId is { } taskId) targetUrl += $"&notificationTaskId={taskId}";

        return JsonSerializer.Serialize(new
        {
            title = notification.Title,
            body = notification.BodyPreview,
            targetUrl,
            // Retained while existing service workers age out. Both values are
            // generated exclusively from persisted notification identifiers.
            url = targetUrl,
            tag = $"project-tracker-notification-{notification.Id}",
            notificationId = notification.Id,
            projectId = notification.ProjectId,
            kind = notification.Kind.ToString(),
            projectTaskId = notification.ProjectTaskId,
            icon = "/brand/son-aero-mark.png",
            badge = "/brand/son-aero-mark.png",
            data = new
            {
                targetUrl,
                notificationId = notification.Id,
                projectId = notification.ProjectId,
                kind = notification.Kind.ToString(),
                projectTaskId = notification.ProjectTaskId
            }
        });
    }
}
