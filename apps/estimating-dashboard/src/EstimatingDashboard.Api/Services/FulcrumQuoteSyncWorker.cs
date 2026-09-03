using Microsoft.Extensions.Options;

namespace EstimatingDashboard.Api.Services;

public sealed class EnterpriseQuoteSyncScheduleOptions
{
    public const string SectionName = "EnterpriseQuoteSync";

    public bool Enabled { get; set; }
    public string TimeZoneId { get; set; } = "Mountain Standard Time";
    public int IntervalMinutes { get; set; } = 30;

    public TimeSpan Interval => TimeSpan.FromMinutes(IntervalMinutes);

    internal void BindConfiguration(IConfiguration configuration)
    {
        var enterpriseSection = configuration.GetSection(SectionName);
        if (enterpriseSection.Exists())
        {
            enterpriseSection.Bind(this);
        }
        else
        {
            // Keep defaults in this class, not the base JSON: a base EnterpriseQuoteSync
            // section would mask legacy enablement in preserved Production settings.
            var legacySection = configuration.GetSection(FulcrumQuoteSyncOptions.SectionName);
            Enabled = legacySection.GetValue("Enabled", false);
            TimeZoneId = legacySection.GetValue("TimeZoneId", TimeZoneId) ?? TimeZoneId;
            IntervalMinutes = legacySection.GetValue("IntervalMinutes", IntervalMinutes);
        }

        if (IntervalMinutes is < 5 or > 1440)
            throw new InvalidOperationException(
                "EnterpriseQuoteSync:IntervalMinutes must be between 5 and 1440 minutes.");
    }
}

internal static class FulcrumQuoteSchedule
{
    public static DateTimeOffset NextRunUtc(DateTimeOffset utcNow, TimeSpan interval)
    {
        if (interval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(interval));

        var utcTicks = utcNow.ToUniversalTime().UtcDateTime.Ticks;
        var remainder = utcTicks % interval.Ticks;
        var nextTicks = remainder == 0
            ? utcTicks + interval.Ticks
            : utcTicks + interval.Ticks - remainder;
        return new DateTimeOffset(nextTicks, TimeSpan.Zero);
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
    IOptions<EnterpriseQuoteSyncScheduleOptions> options,
    TimeProvider timeProvider,
    ILogger<FulcrumQuoteSyncWorker> logger) : BackgroundService
{
    private static readonly TimeSpan MaximumStartDelay = TimeSpan.FromMinutes(15);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = options.Value;
        if (!settings.Enabled)
        {
            logger.LogInformation("Scheduled enterprise quote synchronization is disabled.");
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
                "Scheduled enterprise quote synchronization cannot start because timezone '{TimeZoneId}' is unavailable.",
                settings.TimeZoneId);
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            var now = timeProvider.GetUtcNow();
            var scheduledForUtc = FulcrumQuoteSchedule.NextRunUtc(now, settings.Interval);
            var localSchedule = TimeZoneInfo.ConvertTime(scheduledForUtc, timeZone);
            logger.LogInformation(
                "Next enterprise quote synchronization is scheduled for {ScheduledLocalTime} ({TimeZoneId}).",
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
                    "Skipped the enterprise quote synchronization scheduled for {ScheduledForUtc} because the application resumed more than {MaximumDelayMinutes} minutes late.",
                    scheduledForUtc,
                    MaximumStartDelay.TotalMinutes);
                continue;
            }

            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var sync = scope.ServiceProvider.GetRequiredService<EnterpriseQuoteSyncService>();
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
                    "The enterprise quote synchronization scheduled for {ScheduledForUtc} failed. The next pull remains scheduled for the next {IntervalMinutes}-minute boundary.",
                    scheduledForUtc,
                    settings.IntervalMinutes);
            }
        }
    }
}
