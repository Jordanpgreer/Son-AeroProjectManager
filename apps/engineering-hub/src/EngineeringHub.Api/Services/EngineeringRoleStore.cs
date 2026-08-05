using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using EngineeringHub.Api.Auth;
using EngineeringHub.Api.Data;
using SonAero.Platform.Security;

namespace EngineeringHub.Api.Services;

public sealed record EngineeringModuleAccess(
    string Role,
    bool IsEnabled,
    IReadOnlyList<string> Permissions,
    IReadOnlyList<string> Groups);

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
            var user = await db.Users
                .AsNoTracking()
                .Where(candidate => lookupKeys.Contains(candidate.AccountName.ToUpper()))
                .Select(candidate => new
                {
                    candidate.IsActive,
                    Groups = candidate.GroupMemberships
                        .Select(membership => membership.Group.Name)
                        .ToList(),
                    Permissions = candidate.GroupMemberships
                        .SelectMany(membership => membership.Group.Permissions)
                        .Where(permission => permission.PermissionKey.StartsWith("engineering."))
                        .Select(permission => permission.PermissionKey)
                        .ToList()
                })
                .FirstOrDefaultAsync(cancellationToken);

            if (user is null)
            {
                return null;
            }

            var permissions = EngineeringPermissions.Expand(user.Permissions)
                .OrderBy(permission => permission)
                .ToArray();
            var role = EngineeringPermissions.RoleFor(permissions) ?? ApplicationRoles.Viewer;
            return new EngineeringModuleAccess(
                role,
                user.IsActive && permissions.Contains(EngineeringPermissions.ModuleView, StringComparer.OrdinalIgnoreCase),
                permissions,
                user.Groups.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(group => group).ToArray());
        }
        catch (Exception exception) when (exception is DbException or InvalidOperationException)
        {
            logger.LogError(exception, "The shared module access store is unavailable; Engineering access is denied.");
            return null;
        }
    }

}
