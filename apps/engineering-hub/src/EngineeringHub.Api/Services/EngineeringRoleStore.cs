using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using EngineeringHub.Api.Data;

namespace EngineeringHub.Api.Services;

public interface IEngineeringRoleStore
{
    Task<string?> FindRoleAsync(string accountName, CancellationToken cancellationToken = default);
}

public sealed class EngineeringRoleStore(EngineeringRoleDbContext db, ILogger<EngineeringRoleStore> logger) : IEngineeringRoleStore
{
    public async Task<string?> FindRoleAsync(string accountName, CancellationToken cancellationToken = default)
    {
        try
        {
            var normalized = accountName.ToUpperInvariant();
            return await db.Users
                .AsNoTracking()
                .Where(user => user.AccountName.ToUpper() == normalized)
                .Select(user => user.Role)
                .FirstOrDefaultAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is DbException or InvalidOperationException)
        {
            logger.LogWarning(exception, "The shared application role store is unavailable; engineering hub configuration will be used as a fallback.");
            return null;
        }
    }
}
