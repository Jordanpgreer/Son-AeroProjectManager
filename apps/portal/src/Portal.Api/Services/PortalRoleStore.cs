using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Portal.Api.Data;

namespace Portal.Api.Services;

public interface IPortalRoleStore
{
    Task<string?> FindRoleAsync(string accountName, CancellationToken cancellationToken = default);
    Task<IReadOnlyDictionary<string, string>> FindModuleRolesAsync(
        string accountName,
        CancellationToken cancellationToken = default);
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

    public async Task<IReadOnlyDictionary<string, string>> FindModuleRolesAsync(
        string accountName,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var normalized = accountName.ToUpper();
            var assignments = await db.UserModuleAccess
                .AsNoTracking()
                .Where(access =>
                    access.User.IsActive
                    && access.User.AccountName.ToUpper() == normalized
                    && access.Role != null)
                .Select(access => new { access.ModuleKey, access.Role })
                .ToListAsync(cancellationToken);
            return assignments
                .Where(access =>
                    SonAero.Platform.Security.ApplicationModules.Normalize(access.ModuleKey) is not null
                    && SonAero.Platform.Security.ApplicationModuleRoles.Normalize(access.Role) is not null)
                .ToDictionary(
                    access => SonAero.Platform.Security.ApplicationModules.Normalize(access.ModuleKey)!,
                    access => SonAero.Platform.Security.ApplicationModuleRoles.Normalize(access.Role)!,
                    StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is DbException or InvalidOperationException)
        {
            logger.LogWarning(
                exception,
                "The shared module access store is unavailable; module cards will use the safe fallback.");
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
    }
}
