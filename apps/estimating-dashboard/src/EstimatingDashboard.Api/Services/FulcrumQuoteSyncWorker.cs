using Microsoft.Extensions.Options;

namespace EstimatingDashboard.Api.Services;

internal static class FulcrumQuoteSchedule
{
    private static readonly TimeOnly[] RunTimes =
    [
        new(2, 0),
        new(19, 0)
    ];

    public static DateTimeOffset NextRunUtc(DateTimeOffset utcNow, TimeZoneInfo timeZone)
    {
        var localNow = TimeZoneInfo.ConvertTime(utcNow, timeZone);
        for (var dayOffset = 0; dayOffset <= 2; dayOffset++)
        {
            var date = DateOnly.FromDateTime(localNow.Date).AddDays(dayOffset);
            foreach (var runTime in RunTimes)
            {
                var localCandidate = DateTime.SpecifyKind(date.ToDateTime(runTime), DateTimeKind.Unspecified);
                if (timeZone.IsInvalidTime(localCandidate))
                    continue;
                var candidate = new DateTimeOffset(localCandidate, timeZone.GetUtcOffset(localCandidate));
                if (candidate.ToUniversalTime() > utcNow.ToUniversalTime())
                    return candidate.ToUniversalTime();
            }
        }
        throw new InvalidOperationException("Unable to determine the next Fulcrum quote synchronization time.");
    }

    public static TimeZoneInfo ResolveTimeZone(string configuredId)
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(configuredId);
        }
        catch (TimeZoneNotFoundException) when (string.Equals(
            configuredId,
            "Mountain Standard Time",
            StringComparison.OrdinalIgnoreCase))
        {
            return TimeZoneInfo.FindSystemTimeZoneById("America/Denver");
        }
    }
}

internal sealed class FulcrumQuoteSyncWorker(
    IServiceScopeFactory scopeFactory,
    IOptions<FulcrumQuoteSyncOptions> options,
    TimeProvider timeProvider,
    ILogger<FulcrumQuoteSyncWorker> logger) : BackgroundService
{
    private static readonly TimeSpan MaximumStartDelay = TimeSpan.FromMinutes(15);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = options.Value;
        if (!settings.Enabled)
        {
            logger.LogInformation("Scheduled Fulcrum quote synchronization is disabled.");
            return;
        }
        TimeZoneInfo timeZone;
        try
        {
            timeZone = FulcrumQuoteSchedule.ResolveTimeZone(settings.TimeZoneId);
        }
        catch (TimeZoneNotFoundException exception)
        {
            logger.LogError(
                exception,
                "Scheduled Fulcrum quote synchronization cannot start because timezone '{TimeZoneId}' is unavailable.",
                settings.TimeZoneId);
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            var now = timeProvider.GetUtcNow();
            var scheduledForUtc = FulcrumQuoteSchedule.NextRunUtc(now, timeZone);
            var localSchedule = TimeZoneInfo.ConvertTime(scheduledForUtc, timeZone);
            logger.LogInformation(
                "Next Fulcrum quote synchronization is scheduled for {ScheduledLocalTime} ({TimeZoneId}).",
                localSchedule,
                timeZone.Id);

            try
            {
                await Task.Delay(scheduledForUtc - now, timeProvider, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }

            var startedAt = timeProvider.GetUtcNow();
            if (startedAt - scheduledForUtc > MaximumStartDelay)
            {
                logger.LogWarning(
                    "Skipped the Fulcrum quote synchronization scheduled for {ScheduledForUtc} because the application resumed more than {MaximumDelayMinutes} minutes late.",
                    scheduledForUtc,
                    MaximumStartDelay.TotalMinutes);
                continue;
            }

            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var sync = scope.ServiceProvider.GetRequiredService<FulcrumQuoteSyncService>();
                await sync.RunScheduledAsync(scheduledForUtc, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                logger.LogError(
                    exception,
                    "The Fulcrum quote synchronization scheduled for {ScheduledForUtc} failed. It will not retry outside the configured synchronization times.",
                    scheduledForUtc);
            }
        }
    }
}
