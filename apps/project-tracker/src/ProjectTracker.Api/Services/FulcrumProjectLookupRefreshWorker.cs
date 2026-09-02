using Microsoft.Extensions.Options;
using SonAero.Platform.Integrations;

namespace ProjectTracker.Api.Services;

public sealed class FulcrumProjectLookupRefreshWorker(
    IServiceScopeFactory scopeFactory,
    TimeProvider timeProvider,
    IOptions<ProjectQuantitySyncOptions> options,
    ILogger<FulcrumProjectLookupRefreshWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var timeZone = ResolveTimeZone(options.Value.LookupCatalogTimeZoneId);
        while (!stoppingToken.IsCancellationRequested)
        {
            var nextRefresh = NextRefreshUtc(
                timeProvider.GetUtcNow(),
                timeZone,
                options.Value.LookupCatalogRefreshHours);
            var delay = nextRefresh - timeProvider.GetUtcNow();
            if (delay > TimeSpan.Zero)
                await Task.Delay(delay, timeProvider, stoppingToken);

            try
            {
                await RefreshAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "The scheduled Fulcrum project lookup catalogue refresh failed.");
            }

            await Task.Delay(TimeSpan.FromMinutes(1), timeProvider, stoppingToken);
        }
    }

    private async Task RefreshAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var providerSource = scope.ServiceProvider.GetRequiredService<IEnterpriseProviderSource>();
        var activeProvider = await providerSource.GetActiveProviderAsync(cancellationToken);
        if (!string.Equals(activeProvider, EnterpriseProviderNames.Fulcrum, StringComparison.OrdinalIgnoreCase))
            return;

        var provider = scope.ServiceProvider.GetRequiredService<FulcrumProjectQuantityProvider>();
        await provider.RefreshLookupCatalogAsync(cancellationToken);
        logger.LogInformation("Refreshed the Fulcrum project lookup catalogue.");
    }

    internal static DateTimeOffset NextRefreshUtc(
        DateTimeOffset nowUtc,
        TimeZoneInfo timeZone,
        IEnumerable<int> configuredHours)
    {
        var hours = configuredHours
            .Where(hour => hour is >= 0 and <= 23)
            .Distinct()
            .Order()
            .ToArray();
        if (hours.Length == 0) hours = [5, 8, 11, 14, 17];

        var localNow = TimeZoneInfo.ConvertTime(nowUtc, timeZone);
        for (var dayOffset = 0; dayOffset <= 1; dayOffset++)
        {
            var date = localNow.Date.AddDays(dayOffset);
            foreach (var hour in hours)
            {
                var localCandidate = DateTime.SpecifyKind(date.AddHours(hour), DateTimeKind.Unspecified);
                if (timeZone.IsInvalidTime(localCandidate)) continue;
                var candidateUtc = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(localCandidate, timeZone));
                if (candidateUtc > nowUtc) return candidateUtc;
            }
        }

        throw new InvalidOperationException("A lookup catalogue refresh time could not be calculated.");
    }

    private TimeZoneInfo ResolveTimeZone(string configuredId)
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(configuredId);
        }
        catch (TimeZoneNotFoundException)
        {
            logger.LogWarning(
                "Project lookup time zone {TimeZoneId} was not found. The server local time zone will be used.",
                configuredId);
            return TimeZoneInfo.Local;
        }
        catch (InvalidTimeZoneException)
        {
            logger.LogWarning(
                "Project lookup time zone {TimeZoneId} is invalid. The server local time zone will be used.",
                configuredId);
            return TimeZoneInfo.Local;
        }
    }
}
