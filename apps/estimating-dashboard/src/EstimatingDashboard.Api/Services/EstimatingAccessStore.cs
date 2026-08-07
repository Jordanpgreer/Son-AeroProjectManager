using System.Data.Common;
using EstimatingDashboard.Api.Auth;
using EstimatingDashboard.Api.Data;
using Microsoft.EntityFrameworkCore;
using SonAero.Platform.Security;

namespace EstimatingDashboard.Api.Services;

public interface IEstimatingAccessStore
{
    Task<EstimatingAccessProfile?> FindEnabledAsync(
        string accountName,
        CancellationToken cancellationToken = default);
}

public sealed class EstimatingAccessStore(
    EstimatingAccessDbContext db,
    ILogger<EstimatingAccessStore> logger) : IEstimatingAccessStore
{
    public async Task<EstimatingAccessProfile?> FindEnabledAsync(
        string accountName,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var lookupKeys = WindowsAccountNames.LookupKeys(accountName);
            var record = await db.Users
                .AsNoTracking()
                .Where(user =>
                    user.IsActive
                    && lookupKeys.Contains(user.AccountName.ToUpper()))
                .Select(user => new
                {
                    AppUserId = user.Id,
                    user.AccountName,
                    user.DisplayName
                })
                .SingleOrDefaultAsync(cancellationToken);

            if (record is null) return null;
            var groupIds = await db.UserGroupMemberships.AsNoTracking()
                .Where(membership => membership.AppUserId == record.AppUserId)
                .Select(membership => membership.AppGroupId)
                .ToListAsync(cancellationToken);
            var permissions = await db.GroupPermissions.AsNoTracking()
                .Where(permission => groupIds.Contains(permission.AppGroupId)
                    && permission.PermissionKey.StartsWith("estimating."))
                .Select(permission => permission.PermissionKey)
                .Distinct()
                .ToListAsync(cancellationToken);
            var granted = permissions
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(permission => permission)
                .ToArray();
            var role = RoleFor(granted);
            return role is null
                || !granted.Contains(EstimatingPermissions.View, StringComparer.OrdinalIgnoreCase)
                ? null
                : new EstimatingAccessProfile(
                    record.AppUserId,
                    WindowsAccountNames.Normalize(record.AccountName) ?? record.AccountName,
                    record.DisplayName,
                    role,
                    true,
                    GrantedPermissions: granted);
        }
        catch (Exception exception) when (
            exception is DbException or InvalidOperationException)
        {
            logger.LogError(
                exception,
                "The shared Estimating module access store is unavailable. Access is denied.");
            return null;
        }
    }

    private static string? RoleFor(IReadOnlyCollection<string> permissions)
    {
        if (permissions.Contains(EstimatingPermissions.AdministerRates, StringComparer.OrdinalIgnoreCase)
            || permissions.Contains(EstimatingPermissions.AdministerSettings, StringComparer.OrdinalIgnoreCase))
            return EstimatingRoles.Admin;
        if (permissions.Contains(EstimatingPermissions.ManageQuotes, StringComparer.OrdinalIgnoreCase)
            || permissions.Contains(EstimatingPermissions.ManageInputs, StringComparer.OrdinalIgnoreCase))
            return EstimatingRoles.Editor;
        return permissions.Contains(EstimatingPermissions.View, StringComparer.OrdinalIgnoreCase)
            ? EstimatingRoles.Viewer
            : null;
    }
}
