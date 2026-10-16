using Microsoft.Extensions.Options;
using SonAero.Platform.Integrations;

namespace SmallBusinessSubcontracting.Api;

public sealed class FulcrumVendorSyncWorker(IServiceScopeFactory scopeFactory,
    IOptions<UniversalFulcrumSyncOptions> options, FulcrumScheduledRunCoordinator coordinator,
    TimeProvider clock, ILogger<FulcrumVendorSyncWorker> logger)
    : UniversalFulcrumSyncWorker(options, coordinator, clock, logger)
{
    protected override string ModuleName => "subcontracting-vendors";

    protected override async Task PullAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<VendorService>()
            .SynchronizeAsync("SYSTEM\\Fulcrum", cancellationToken);
    }
}
