using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SonAero.Platform.Notifications;

namespace QualityAssurance.Api.Services;

public sealed class PortalPushOptions
{
    public const string SectionName = "PortalPush";

    public bool Enabled { get; set; }
    public string BaseUrl { get; set; } = string.Empty;
    public string ProducerKey { get; set; } = string.Empty;
}

public interface IArdaPushNotificationPublisher
{
    Task<bool> PublishAsync(
        ArdaPushNotificationRequest notification,
        CancellationToken cancellationToken = default);
}

public sealed class PortalPushNotificationPublisher(
    HttpClient httpClient,
    IOptions<PortalPushOptions> options,
    ILogger<PortalPushNotificationPublisher> logger) : IArdaPushNotificationPublisher
{
    private const string SourceModule = "quality-assurance";

    public async Task<bool> PublishAsync(
        ArdaPushNotificationRequest notification,
        CancellationToken cancellationToken = default)
    {
        var settings = options.Value;
        if (!settings.Enabled) return false;

        if (!Uri.TryCreate(settings.BaseUrl, UriKind.Absolute, out var portalBaseUrl)
            || string.IsNullOrWhiteSpace(settings.ProducerKey))
        {
            logger.LogWarning(
                "Portal Web Push is enabled for Quality Assurance but its Portal URL or producer key is missing.");
            return false;
        }

        try
        {
            var endpoint = new Uri(
                portalBaseUrl,
                $"/api/push/producers/{SourceModule}/notifications");
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Content = JsonContent.Create(notification)
            };
            request.Headers.TryAddWithoutValidation("X-Arda-Push-Key", settings.ProducerKey);
            using var response = await httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning(
                    "Portal Web Push rejected Quality notification {NotificationKey} with HTTP {StatusCode}.",
                    notification.SourceNotificationKey,
                    (int)response.StatusCode);
            }
            return response.IsSuccessStatusCode;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            // The Quality notification is already durable in its own inbox. A
            // temporary Portal outage must not roll back or fail the comment.
            logger.LogWarning(
                exception,
                "Portal Web Push delivery could not be queued for Quality notification {NotificationKey}.",
                notification.SourceNotificationKey);
            return false;
        }
    }
}

public sealed class QualityPortalPushBridgeWorker(
    IServiceScopeFactory scopeFactory,
    IOptions<PortalPushOptions> options,
    TimeProvider timeProvider,
    ILogger<QualityPortalPushBridgeWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15), timeProvider);
        while (!stoppingToken.IsCancellationRequested)
        {
            if (options.Value.Enabled)
            {
                try
                {
                    await PublishPendingAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception exception)
                {
                    logger.LogWarning(exception, "Pending Quality notifications could not be reconciled with Portal Web Push.");
                }
            }

            if (!await timer.WaitForNextTickAsync(stoppingToken)) return;
        }
    }

    public async Task<int> PublishPendingAsync(CancellationToken cancellationToken = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<QualityAssurance.Api.Data.QualityAssuranceDbContext>();
        var publisher = scope.ServiceProvider.GetRequiredService<IArdaPushNotificationPublisher>();
        var pending = await db.MentionNotifications
            .Where(notification => notification.PortalPushPublishedAt == null)
            .OrderBy(notification => notification.Id)
            .Take(100)
            .ToListAsync(cancellationToken);
        if (pending.Count == 0) return 0;

        var pendingShipmentIds = pending
            .Select(notification => notification.ShipmentId)
            .Distinct()
            .ToList();
        var shippedShipmentIds = await db.Shipments
            .Where(shipment => pendingShipmentIds.Contains(shipment.Id)
                && shipment.IsShipped)
            .Select(shipment => shipment.Id)
            .ToListAsync(cancellationToken);
        var shippedIds = shippedShipmentIds.ToHashSet();
        var published = 0;
        foreach (var notification in pending)
        {
            if (!await publisher.PublishAsync(Request(notification, shippedIds.Contains(notification.ShipmentId)), cancellationToken))
                break;
            notification.PortalPushPublishedAt = timeProvider.GetUtcNow();
            published++;
        }
        if (published > 0) await db.SaveChangesAsync(cancellationToken);
        return published;
    }

    public static ArdaPushNotificationRequest Request(
        QualityAssurance.Api.Models.QualityMentionNotification notification,
        bool isShipped) => new(
            $"quality-mention-{notification.Id}",
            notification.RecipientAccountName,
            $"{notification.ActorDisplayName} mentioned you in Quality",
            notification.BodyPreview,
            $"/#/shipping-status?shipment={notification.ShipmentId}&comments=1&notification={notification.Id}&status={(isShipped ? "shipped" : "open")}");
}
