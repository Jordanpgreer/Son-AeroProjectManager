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
    IOptions<SonAero.Platform.Integrations.UniversalFulcrumSyncOptions> options,
    SonAero.Platform.Integrations.FulcrumScheduledRunCoordinator coordinator,
    TimeProvider timeProvider,
    ILogger<FulcrumQuoteSyncWorker> logger)
    : SonAero.Platform.Integrations.UniversalFulcrumSyncWorker(options, coordinator, timeProvider, logger)
{
    protected override string ModuleName => "estimating-quotes";

    protected override async Task PullAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<EnterpriseQuoteSyncService>()
            .RunScheduledAsync(Clock.GetUtcNow(), cancellationToken);
    }
}
