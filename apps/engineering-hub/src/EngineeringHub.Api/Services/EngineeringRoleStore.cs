using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using EngineeringHub.Api.Data;
using SonAero.Platform.Security;

namespace EngineeringHub.Api.Services;

public sealed record EngineeringModuleAccess(string Role, bool IsEnabled);

public interface IEngineeringRoleStore
{
    Task<EngineeringModuleAccess?> FindAccessAsync(string accountName, CancellationToken cancellationToken = default);
}

public sealed class EngineeringRoleStore(EngineeringRoleDbContext db, ILogger<EngineeringRoleStore> logger) : IEngineeringRoleStore
{
    public async Task<EngineeringModuleAccess?> FindAccessAsync(string accountName, CancellationToken cancellationToken = default)
    {
        try
        {
            var lookupKeys = WindowsAccountNames.LookupKeys(accountName);
            var assignment = await db.UserModuleAccess
                .AsNoTracking()
                .Where(access =>
                    access.ModuleKey == ApplicationModules.Engineering
                    && lookupKeys.Contains(access.User.AccountName.ToUpper()))
                .Select(access => new
                {
                    access.User.IsActive,
                    access.Role
                })
                .FirstOrDefaultAsync(cancellationToken);

            var role = ApplicationModuleRoles.Normalize(assignment?.Role);
            return assignment is null
                ? null
                : new EngineeringModuleAccess(role ?? ApplicationRoles.Viewer, assignment.IsActive && role is not null);
        }
        catch (Exception exception) when (exception is DbException or InvalidOperationException)
        {
            logger.LogError(exception, "The shared module access store is unavailable; Engineering access is denied.");
            return null;
        }
    }
}
