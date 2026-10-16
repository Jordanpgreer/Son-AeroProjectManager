using System.Data.Common;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging;

namespace SonAero.Platform.Integrations;

public sealed class UniversalFulcrumSyncOptions
{
    public const string SectionName = "UniversalFulcrumSync";
    public bool Enabled { get; set; } = true;
    public int IntervalMinutes { get; set; } = 20;
    public int MaximumConcurrentRequests { get; set; } = 2;
    public int MinimumRequestSpacingMilliseconds { get; set; } = 250;
    public int MaximumStaleMinutes { get; set; } = 1440;
    public int MaximumResponseBytes { get; set; } = 16 * 1024 * 1024;

    public TimeSpan Interval => TimeSpan.FromMinutes(IntervalMinutes);

    public DateTimeOffset NextBoundary(DateTimeOffset now)
    {
        var ticks = now.UtcTicks;
        return new DateTimeOffset(ticks - ticks % Interval.Ticks + Interval.Ticks, TimeSpan.Zero);
    }
}

public delegate DbConnection FulcrumSnapshotConnectionFactory(IServiceProvider services);

public static class UniversalFulcrumSyncRegistration
{
    // Every module supplies its existing shared role-store connection, never its module-only DB.
    public static IServiceCollection AddUniversalFulcrumSync(this IServiceCollection services,
        IConfiguration configuration, FulcrumSnapshotConnectionFactory connectionFactory)
    {
        services.AddOptions<UniversalFulcrumSyncOptions>()
            .Bind(configuration.GetSection(UniversalFulcrumSyncOptions.SectionName))
            .Validate(o => o.IntervalMinutes is >= 5 and <= 1440
                && o.MaximumConcurrentRequests is >= 1 and <= 8
                && o.MinimumRequestSpacingMilliseconds is >= 100 and <= 10000
                && o.MaximumStaleMinutes is >= 0 and <= 10080
                && o.MaximumResponseBytes is >= 1024 and <= 64 * 1024 * 1024,
                "Universal Fulcrum synchronization settings are outside their supported limits.")
            .ValidateOnStart();
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton(connectionFactory);
        services.AddSingleton<FulcrumSnapshotStore>();
        services.AddTransient<FulcrumReadSnapshotHandler>();
        services.AddSingleton<FulcrumScheduledRunCoordinator>();
        return services;
    }

    public static IHttpClientBuilder UseSharedFulcrumSnapshots(this IHttpClientBuilder builder) =>
        builder.AddHttpMessageHandler<FulcrumReadSnapshotHandler>()
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
}

public sealed class FulcrumScheduledRunCoordinator(FulcrumSnapshotStore store,
    IOptions<UniversalFulcrumSyncOptions> options, TimeProvider clock,
    ILogger<FulcrumScheduledRunCoordinator> logger)
{
    public async Task<bool> RunAsync(string module, Func<CancellationToken, Task> action,
        CancellationToken cancellationToken)
    {
        var key = FulcrumSnapshotStore.Hash("scheduled:" + module);
        var owner = Guid.NewGuid().ToString("N");
        var now = clock.GetUtcNow();
        if (!await store.HasUsableCredentialAsync(now, cancellationToken))
        {
            logger.LogInformation("Skipped {Module} automatic pull: Fulcrum is inactive or its shared credential is missing or expired.", module);
            return false;
        }
        if (!await store.TryAcquireAsync(key, owner, now, TimeSpan.FromHours(2), cancellationToken)) return false;
        try
        {
            // Cancel before the durable lease expires, including very large vendor/project runs.
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromMinutes(90));
            await action(deadline.Token);
            await store.PublishAsync(key, owner, "{}"u8.ToArray(), "application/json", now,
                options.Value.NextBoundary(clock.GetUtcNow()), cancellationToken);
            return true;
        }
        finally
        {
            await store.ReleaseAsync(key, owner, CancellationToken.None);
        }
    }
}
