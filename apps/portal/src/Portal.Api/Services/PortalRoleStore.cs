using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Portal.Api.Data;
using SonAero.Platform.Security;

namespace Portal.Api.Services;

public interface IPortalRoleStore
{
    Task<string?> FindRoleAsync(string accountName, CancellationToken cancellationToken = default);
    Task<string?> FindDisplayNameAsync(string accountName, CancellationToken cancellationToken = default);
    Task<IReadOnlyDictionary<string, string>> FindModuleRolesAsync(
        string accountName,
        CancellationToken cancellationToken = default);
}

public sealed class PortalRoleStore(PortalRoleDbContext db, ILogger<PortalRoleStore> logger) : IPortalRoleStore
{
    public async Task<string?> FindDisplayNameAsync(string accountName, CancellationToken cancellationToken = default)
    {
        try
        {
            var lookupKeys = WindowsAccountNames.LookupKeys(accountName);
            return await db.Users
                .AsNoTracking()
                .Where(user => user.IsActive && lookupKeys.Contains(user.AccountName.ToUpper()))
                .Select(user => user.DisplayName)
                .FirstOrDefaultAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is DbException or InvalidOperationException)
        {
            logger.LogWarning(exception, "The shared application user store is unavailable; the Windows account name will be used as a fallback.");
            return null;
        }
    }

    public async Task<string?> FindRoleAsync(string accountName, CancellationToken cancellationToken = default)
    {
        try
        {
            var lookupKeys = WindowsAccountNames.LookupKeys(accountName);
            return await db.Users
                .AsNoTracking()
                .Where(user => user.IsActive && lookupKeys.Contains(user.AccountName.ToUpper()))
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
            var lookupKeys = WindowsAccountNames.LookupKeys(accountName);
            var user = await db.Users
                .AsNoTracking()
                .Where(candidate =>
                    candidate.IsActive
                    && lookupKeys.Contains(candidate.AccountName.ToUpper()))
                .Select(candidate => new { candidate.Id })
                .SingleOrDefaultAsync(cancellationToken);
            if (user is null)
                return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            var assignments = await db.UserModuleAccess.AsNoTracking()
                .Where(access => access.AppUserId == user.Id && access.Role != null)
                .Select(access => new { access.ModuleKey, access.Role })
                .ToListAsync(cancellationToken);
            var groupIds = await db.ProjectTrackerUserGroupMemberships.AsNoTracking()
                .Where(membership => membership.AppUserId == user.Id)
                .Select(membership => membership.AppGroupId)
                .ToListAsync(cancellationToken);
            var permissions = await db.ProjectTrackerGroupPermissions.AsNoTracking()
                .Where(permission => groupIds.Contains(permission.AppGroupId))
                .Select(permission => permission.PermissionKey)
                .Distinct()
                .ToListAsync(cancellationToken);

            var roles = assignments
                .Where(access =>
                    SonAero.Platform.Security.ApplicationModules.Normalize(access.ModuleKey) is not null
                    && SonAero.Platform.Security.ApplicationModuleRoles.Normalize(access.Role) is not null)
                .ToDictionary(
                    access => SonAero.Platform.Security.ApplicationModules.Normalize(access.ModuleKey)!,
                    access => SonAero.Platform.Security.ApplicationModuleRoles.Normalize(access.Role)!,
                    StringComparer.OrdinalIgnoreCase);

            var engineeringRole = EngineeringPermissions.RoleFor(permissions);
            if (engineeringRole is not null) roles[ApplicationModules.Engineering] = engineeringRole;
            foreach (var moduleKey in new[] { ApplicationModules.Estimating, ApplicationModules.QualityAssurance })
            {
                var role = RoleForGrantedModulePermissions(moduleKey, permissions);
                if (role is not null) roles[moduleKey] = role;
            }

            return roles;
        }
        catch (Exception exception) when (exception is DbException or InvalidOperationException)
        {
            logger.LogWarning(
                exception,
                "The shared module access store is unavailable; module cards will use the safe fallback.");
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static string? RoleForGrantedModulePermissions(
        string moduleKey,
        IReadOnlyCollection<string> permissions)
    {
        var role = ApplicationModuleCatalog.RoleForPermissions(moduleKey, permissions);
        if (role is not null) return role;
        var entryPermission = moduleKey == ApplicationModules.QualityAssurance
            ? QualityAssurancePermissions.ModuleView
            : "estimating.view";
        return permissions.Contains(entryPermission, StringComparer.OrdinalIgnoreCase)
            ? ApplicationRoles.Viewer
            : null;
    }
}
