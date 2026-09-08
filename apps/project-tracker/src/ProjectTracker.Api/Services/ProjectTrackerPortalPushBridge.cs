using System.Net.Http.Json;
using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ProjectTracker.Api.Configuration;
using ProjectTracker.Api.Data;
using ProjectTracker.Api.Models;
using SonAero.Platform.Notifications;

namespace ProjectTracker.Api.Services;

public interface IProjectTrackerPortalPushBridge
{
    bool TryEnqueue(int notificationId);
}

public interface IProjectTrackerPortalPushPublisher
{
    Task<bool> PublishAsync(
        ArdaPushNotificationRequest notification,
        CancellationToken cancellationToken = default);
}

public sealed class ProjectTrackerPortalPushPublisher(
    HttpClient httpClient,
    IOptions<PortalPushOptions> options,
    ILogger<ProjectTrackerPortalPushPublisher> logger) : IProjectTrackerPortalPushPublisher
{
    private const string SourceModule = "project-tracker";

    public async Task<bool> PublishAsync(
        ArdaPushNotificationRequest notification,
        CancellationToken cancellationToken = default)
    {
        var settings = options.Value;
        if (!settings.Enabled) return false;
        if (!Uri.TryCreate(settings.BaseUrl, UriKind.Absolute, out var portalBaseUrl)
            || string.IsNullOrWhiteSpace(settings.ProducerKey)) return false;

        try
        {
            var endpoint = new Uri(portalBaseUrl, $"/api/push/producers/{SourceModule}/notifications");
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Content = JsonContent.Create(notification)
            };
            request.Headers.TryAddWithoutValidation("X-Arda-Push-Key", settings.ProducerKey);
            using var response = await httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning(
                    "Portal Web Push rejected Project Tracker notification {NotificationKey} with HTTP {StatusCode}.",
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
            logger.LogWarning(
                exception,
                "Portal Web Push delivery could not be queued for Project Tracker notification {NotificationKey}.",
                notification.SourceNotificationKey);
            return false;
        }
    }
}

public sealed class ProjectTrackerPortalPushBridgeWorker(
    IServiceScopeFactory scopeFactory,
    IOptions<PortalPushOptions> options,
    TimeProvider timeProvider,
    ILogger<ProjectTrackerPortalPushBridgeWorker> logger)
    : BackgroundService, IProjectTrackerPortalPushBridge
{
    private readonly Channel<int> wakeups = Channel.CreateBounded<int>(new BoundedChannelOptions(256)
    {
        FullMode = BoundedChannelFullMode.DropOldest,
        SingleReader = true,
        SingleWriter = false
    });

    public bool TryEnqueue(int notificationId) =>
        notificationId > 0 && wakeups.Writer.TryWrite(notificationId);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
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
                    logger.LogWarning(exception, "Pending Project Tracker notifications could not be reconciled with Portal Web Push.");
                }
            }

            var wakeup = wakeups.Reader.WaitToReadAsync(stoppingToken).AsTask();
            var delay = Task.Delay(TimeSpan.FromSeconds(15), timeProvider, stoppingToken);
            await Task.WhenAny(wakeup, delay);
            while (wakeups.Reader.TryRead(out _)) { }
        }
    }

    public async Task<int> PublishPendingAsync(CancellationToken cancellationToken = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ProjectTrackerDbContext>();
        var publisher = scope.ServiceProvider.GetRequiredService<IProjectTrackerPortalPushPublisher>();
        var pending = await db.UserNotifications
            .AsNoTracking()
            .Include(notification => notification.RecipientUser)
            .Where(notification => notification.PortalPushPublishedAt == null)
            .OrderBy(notification => notification.Id)
            .Take(100)
            .ToListAsync(cancellationToken);
        if (pending.Count == 0) return 0;

        var published = 0;
        foreach (var notification in pending)
        {
            if (!await publisher.PublishAsync(Request(notification), cancellationToken)) break;
            var marked = await db.UserNotifications
                .IgnoreQueryFilters()
                .Where(candidate => candidate.Id == notification.Id
                    && candidate.PortalPushPublishedAt == null)
                .ExecuteUpdateAsync(setters => setters.SetProperty(
                    candidate => candidate.PortalPushPublishedAt,
                    timeProvider.GetUtcNow()), cancellationToken);
            published += marked;
        }
        return published;
    }

    public static ArdaPushNotificationRequest Request(UserNotification notification) => new(
        $"project-tracker-notification-{notification.Id}-{notification.CreatedAt.UtcTicks}",
        notification.RecipientUser.AccountName,
        notification.Title,
        notification.BodyPreview,
        PushNotificationWorker.TargetUrl(notification));
}
