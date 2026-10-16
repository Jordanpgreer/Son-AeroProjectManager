using Microsoft.Extensions.Options;
using SonAero.Platform.Integrations;

namespace ProjectTracker.Api.Services;

public sealed class FulcrumProjectLookupRefreshWorker(
    IServiceScopeFactory scopeFactory,
    TimeProvider timeProvider,
    IOptions<UniversalFulcrumSyncOptions> options,
    FulcrumScheduledRunCoordinator coordinator,
    ILogger<FulcrumProjectLookupRefreshWorker> logger)
    : UniversalFulcrumSyncWorker(options, coordinator, timeProvider, logger)
{
    protected override string ModuleName => "project-tracker";

    protected override async Task PullAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<FulcrumProjectQuantityProvider>()
            .RefreshLookupCatalogAsync(cancellationToken);
        await scope.ServiceProvider.GetRequiredService<ProjectQuantityScheduledSyncService>()
            .RunAsync(cancellationToken);
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

}
