using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace SonAero.Platform.Integrations;

public abstract class UniversalFulcrumSyncWorker(IOptions<UniversalFulcrumSyncOptions> options,
    FulcrumScheduledRunCoordinator coordinator, TimeProvider clock, ILogger logger) : BackgroundService
{
    protected TimeProvider Clock => clock;
    protected ILogger Log => logger;
    protected abstract string ModuleName { get; }
    protected virtual bool IsEnabled => true;
    protected abstract Task PullAsync(CancellationToken cancellationToken);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled || !IsEnabled) return;
        // No startup API burst. All processes use the same UTC :00/:20/:40 boundaries.
        while (!stoppingToken.IsCancellationRequested)
        {
            var next = options.Value.NextBoundary(clock.GetUtcNow());
            try
            {
                var delay = next - clock.GetUtcNow();
                if (delay > TimeSpan.Zero) await Task.Delay(delay, clock, stoppingToken);
                var started = clock.GetUtcNow();
                if (await coordinator.RunAsync(ModuleName, PullAsync, stoppingToken))
                    logger.LogInformation("Automatic {Module} pull completed in {Seconds:F1}s; interval {Minutes} minutes.",
                        ModuleName, (clock.GetUtcNow() - started).TotalSeconds, options.Value.IntervalMinutes);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Automatic {Module} pull failed; existing Arda records are retained. The next attempt uses the shared schedule.", ModuleName);
            }
        }
    }
}
