using Microsoft.EntityFrameworkCore;
using Portal.Api.Data;
using SonAero.Platform.Security;

namespace Portal.Api.Services;

public sealed class PortalIntegrationCredentialSchemaInitializer(PortalRoleDbContext db)
{
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        var provider = db.Database.ProviderName ?? string.Empty;
        if (provider.Contains("Sqlite", StringComparison.OrdinalIgnoreCase))
            await db.Database.ExecuteSqlRawAsync(IntegrationCredentialSchema.Sqlite, cancellationToken);
        else if (provider.Contains("SqlServer", StringComparison.OrdinalIgnoreCase))
            await db.Database.ExecuteSqlRawAsync(IntegrationCredentialSchema.SqlServer, cancellationToken);
    }
}
