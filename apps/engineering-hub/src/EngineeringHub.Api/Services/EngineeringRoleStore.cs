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
    IReadOnlyList<string> Groups,
    string? AccountName = null,
    string? DisplayName = null,
    bool IsPreview = false,
    string? PreviewActorAccountName = null,
    string? PreviewTargetKey = null,
    string? PreviewTargetTitle = null);

public interface IEngineeringRoleStore
{
    Task<EngineeringModuleAccess?> FindAccessAsync(string accountName, CancellationToken cancellationToken = default);

    async Task<IReadOnlyDictionary<string, string>> FindDisplayNamesAsync(
        IReadOnlyCollection<string> accountNames,
        CancellationToken cancellationToken = default)
    {
        var displayNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var accountName in accountNames)
        {
            var normalized = WindowsAccountNames.Normalize(accountName);
            if (normalized is null) continue;
            var access = await FindAccessAsync(normalized, cancellationToken);
            if (!string.IsNullOrWhiteSpace(access?.DisplayName))
                displayNames[normalized] = access.DisplayName.Trim();
        }
        return displayNames;
    }
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
                    candidate.AccountName,
                    candidate.DisplayName,
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
                user.Groups.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(group => group).ToArray(),
                WindowsAccountNames.Normalize(user.AccountName) ?? user.AccountName,
                user.DisplayName);
        }
        catch (Exception exception) when (exception is DbException or InvalidOperationException)
        {
            logger.LogError(exception, "The shared module access store is unavailable; Engineering access is denied.");
            return null;
        }
    }

    public async Task<IReadOnlyDictionary<string, string>> FindDisplayNamesAsync(
        IReadOnlyCollection<string> accountNames,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var normalizedAccounts = accountNames
                .Select(WindowsAccountNames.Normalize)
                .Where(account => account is not null)
                .Select(account => account!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (normalizedAccounts.Length == 0)
                return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            var lookupKeys = normalizedAccounts
                .SelectMany(WindowsAccountNames.LookupKeys)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var users = await db.Users.AsNoTracking()
                .Where(user => lookupKeys.Contains(user.AccountName.ToUpper()))
                .Select(user => new { user.AccountName, user.DisplayName })
                .ToListAsync(cancellationToken);

            return users
                .Select(user => new
                {
                    AccountName = WindowsAccountNames.Normalize(user.AccountName),
                    DisplayName = user.DisplayName?.Trim()
                })
                .Where(user => user.AccountName is not null && !string.IsNullOrWhiteSpace(user.DisplayName))
                .GroupBy(user => user.AccountName!, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First().DisplayName!, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is DbException or InvalidOperationException)
        {
            logger.LogError(exception, "The shared user directory is unavailable; account names will use local display fallbacks.");
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
    }

}
