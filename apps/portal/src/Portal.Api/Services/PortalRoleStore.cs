using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Portal.Api.Data;
using SonAero.Platform.Security;

namespace Portal.Api.Services;

public interface IPortalRoleStore
{
    Task<PortalAccountLookup> FindAccountAsync(
        string accountName,
        CancellationToken cancellationToken = default);

    Task<PortalAccountLookup> RegisterPendingAccountAsync(
        string accountName,
        string displayName,
        CancellationToken cancellationToken = default);
}

public enum PortalAccountLookupStatus
{
    Found,
    Missing,
    Unavailable
}

public sealed record PortalAccountLookup(
    PortalAccountLookupStatus Status,
    bool IsActive,
    string? Role,
    string? DisplayName,
    bool HasProjectTrackerAccess,
    IReadOnlyDictionary<string, string> ModuleRoles)
{
    public static PortalAccountLookup Missing() => new(
        PortalAccountLookupStatus.Missing,
        false,
        null,
        null,
        false,
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));

    public static PortalAccountLookup Unavailable() => new(
        PortalAccountLookupStatus.Unavailable,
        false,
        null,
        null,
        false,
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
}

public sealed class PortalRoleStore(PortalRoleDbContext db, ILogger<PortalRoleStore> logger) : IPortalRoleStore
{
    public async Task<PortalAccountLookup> FindAccountAsync(
        string accountName,
        CancellationToken cancellationToken = default)
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
                    candidate.Role,
                    candidate.DisplayName,
                    ModuleAssignments = candidate.ModuleAccessAssignments
                        .Where(access => access.Role != null)
                        .Select(access => new { access.ModuleKey, access.Role })
                        .ToList(),
                    Permissions = candidate.ProjectTrackerGroupMemberships
                        .SelectMany(membership => membership.Group.Permissions)
                        .Select(permission => permission.PermissionKey)
                        .ToList()
                })
                .FirstOrDefaultAsync(cancellationToken);
            if (user is null)
                return PortalAccountLookup.Missing();

            var roles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var assignment in user.ModuleAssignments)
            {
                var moduleKey = ApplicationModules.Normalize(assignment.ModuleKey);
                var role = ApplicationModuleRoles.Normalize(assignment.Role);
                var module = ApplicationModuleCatalog.Find(moduleKey);
                if (moduleKey is null
                    || role is null
                    || module?.Roles.Any(candidate => candidate.Role == role) != true)
                {
                    continue;
                }

                roles[moduleKey] = role;
            }

            var engineeringRole = EngineeringPermissions.RoleFor(user.Permissions);
            if (engineeringRole is not null) roles[ApplicationModules.Engineering] = engineeringRole;
            foreach (var moduleKey in new[] { ApplicationModules.Estimating, ApplicationModules.QualityAssurance })
            {
                var role = RoleForGrantedModulePermissions(moduleKey, user.Permissions);
                if (role is not null) roles[moduleKey] = role;
            }

            return new PortalAccountLookup(
                PortalAccountLookupStatus.Found,
                user.IsActive,
                user.Role,
                user.DisplayName,
                user.Permissions.Contains(ApplicationPermissions.ModuleView, StringComparer.OrdinalIgnoreCase),
                roles);
        }
        catch (Exception exception) when (exception is DbException or InvalidOperationException)
        {
            logger.LogError(
                exception,
                "The shared application access store is unavailable; Portal access is denied unless a bootstrap role is configured.");
            return PortalAccountLookup.Unavailable();
        }
    }

    public async Task<PortalAccountLookup> RegisterPendingAccountAsync(
        string accountName,
        string displayName,
        CancellationToken cancellationToken = default)
    {
        var normalizedAccount = WindowsAccountNames.Normalize(accountName);
        if (normalizedAccount is null)
            return PortalAccountLookup.Unavailable();

        var existing = await FindAccountAsync(normalizedAccount, cancellationToken);
        if (existing.Status != PortalAccountLookupStatus.Missing)
            return existing;

        var pending = new PortalRoleRecord
        {
            AccountName = normalizedAccount,
            DisplayName = displayName.Trim(),
            Role = ApplicationRoles.Viewer,
            IsActive = true,
            LastSeenAt = DateTimeOffset.UtcNow
        };
        db.Users.Add(pending);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception)
        {
            db.Entry(pending).State = EntityState.Detached;
            var concurrentlyCreated = await FindAccountAsync(normalizedAccount, cancellationToken);
            if (concurrentlyCreated.Status == PortalAccountLookupStatus.Found)
                return concurrentlyCreated;

            logger.LogError(exception,
                "Could not register pending Arda account {AccountName}; access remains denied.",
                normalizedAccount);
            return PortalAccountLookup.Unavailable();
        }

        logger.LogInformation("Registered pending Arda account {AccountName} on first sign-in.", normalizedAccount);
        return await FindAccountAsync(normalizedAccount, cancellationToken);
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
