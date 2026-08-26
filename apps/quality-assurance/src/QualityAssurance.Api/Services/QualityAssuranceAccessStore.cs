using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using QualityAssurance.Api.Auth;
using QualityAssurance.Api.Data;
using SonAero.Platform.Security;

namespace QualityAssurance.Api.Services;

public interface IQualityAssuranceAccessStore
{
    Task<QualityAssuranceAccessProfile?> FindAccessAsync(
        string accountName,
        CancellationToken cancellationToken = default);
    Task<IReadOnlyList<QualityDirectoryGroup>> GetGroupsAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<QualityDirectoryUser>> GetUsersAsync(int? groupId = null, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<QualityDirectoryUser>> GetUsersWithPermissionAsync(
        string permissionKey,
        CancellationToken cancellationToken = default);
}

public sealed record QualityDirectoryGroup(int Id, string Name, string? Description, int ActiveUserCount);
public sealed record QualityDirectoryUser(int Id, string AccountName, string DisplayName, IReadOnlyList<int> GroupIds);

public sealed class QualityAssuranceAccessStore(
    QualityAssuranceAccessDbContext db,
    ILogger<QualityAssuranceAccessStore> logger) : IQualityAssuranceAccessStore
{
    public async Task<QualityAssuranceAccessProfile?> FindAccessAsync(
        string accountName,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var lookupKeys = WindowsAccountNames.LookupKeys(accountName);
            var record = await db.Users
                .AsNoTracking()
                .Include(user => user.GroupMemberships)
                    .ThenInclude(membership => membership.Group)
                        .ThenInclude(group => group.Permissions)
                .Where(user =>
                    user.IsActive
                    && lookupKeys.Contains(user.AccountName.ToUpper())
                    && user.GroupMemberships.Any(membership =>
                        membership.Group.Permissions.Any(permission =>
                            permission.PermissionKey == QualityAssurancePermissions.ModuleView)))
                .SingleOrDefaultAsync(cancellationToken);

            if (record is null) return null;
            var permissions = record.GroupMemberships
                .SelectMany(membership => membership.Group.Permissions)
                .Select(permission => permission.PermissionKey)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            var role = ApplicationModuleCatalog.RoleForPermissions(
                ApplicationModules.QualityAssurance,
                permissions) ?? ApplicationRoles.Viewer;
            return new QualityAssuranceAccessProfile(
                record.Id,
                WindowsAccountNames.Normalize(record.AccountName) ?? record.AccountName,
                record.DisplayName,
                role,
                permissions,
                record.GroupMemberships
                    .Select(membership => new QualityAssuranceAccessGroup(membership.Group.Id, membership.Group.Name))
                    .ToList());
        }
        catch (Exception exception) when (exception is DbException or InvalidOperationException)
        {
            logger.LogError(
                exception,
                "The shared Quality Assurance access store is unavailable. Access is denied.");
            return null;
        }
    }

    public async Task<IReadOnlyList<QualityDirectoryGroup>> GetGroupsAsync(
        CancellationToken cancellationToken = default) =>
        await db.Groups
            .AsNoTracking()
            .OrderBy(group => group.Name)
            .Select(group => new QualityDirectoryGroup(
                group.Id,
                group.Name,
                group.Description,
                group.UserMemberships.Count(membership => membership.User.IsActive)))
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<QualityDirectoryUser>> GetUsersAsync(
        int? groupId = null,
        CancellationToken cancellationToken = default)
    {
        var users = await db.Users
            .AsNoTracking()
            .Include(user => user.GroupMemberships)
            .Where(user => user.IsActive
                && (!groupId.HasValue || user.GroupMemberships.Any(membership => membership.AppGroupId == groupId)))
            .OrderBy(user => user.DisplayName)
            .ThenBy(user => user.AccountName)
            .ToListAsync(cancellationToken);
        return users.Select(user => new QualityDirectoryUser(
                user.Id,
                user.AccountName,
                user.DisplayName,
                user.GroupMemberships.Select(membership => membership.AppGroupId).ToList()))
            .ToList();
    }

    public async Task<IReadOnlyList<QualityDirectoryUser>> GetUsersWithPermissionAsync(
        string permissionKey,
        CancellationToken cancellationToken = default)
    {
        var users = await db.Users
            .AsNoTracking()
            .Include(user => user.GroupMemberships)
            .Where(user => user.IsActive
                && user.GroupMemberships.Any(membership =>
                    membership.Group.Permissions.Any(permission => permission.PermissionKey == permissionKey)))
            .OrderBy(user => user.DisplayName)
            .ThenBy(user => user.AccountName)
            .ToListAsync(cancellationToken);
        return users.Select(user => new QualityDirectoryUser(
                user.Id,
                user.AccountName,
                user.DisplayName,
                user.GroupMemberships.Select(membership => membership.AppGroupId).ToList()))
            .ToList();
    }
}
