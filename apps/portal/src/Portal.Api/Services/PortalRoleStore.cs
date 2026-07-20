using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Portal.Api.Data;

namespace Portal.Api.Services;

public interface IPortalRoleStore
{
    Task<string?> FindRoleAsync(string accountName, CancellationToken cancellationToken = default);
}

public sealed class PortalRoleStore(PortalRoleDbContext db, ILogger<PortalRoleStore> logger) : IPortalRoleStore
{
    public async Task<string?> FindRoleAsync(string accountName, CancellationToken cancellationToken = default)
    {
        try
        {
            var normalized = accountName.ToUpper();
            return await db.Users
                .AsNoTracking()
                .Where(user => user.AccountName.ToUpper() == normalized)
                .Select(user => user.Role)
                .FirstOrDefaultAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is DbException or InvalidOperationException)
        {
            logger.LogWarning(exception, "The shared application role store is unavailable; portal configuration will be used as a fallback.");
            return null;
        }
    }
}
