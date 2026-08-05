using EngineeringHub.Api.Auth;
using EngineeringHub.Api.Data;
using EngineeringHub.Api.Dtos;
using Microsoft.EntityFrameworkCore;
using SonAero.Platform.Security;

namespace EngineeringHub.Api.Endpoints;

public static class EngineeringAccessEndpoints
{
    public static void MapEngineeringAccessEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/engineering-access", GetOverviewAsync)
            .RequireAuthorization(EngineeringPermissions.SettingsView);
        api.MapPut("/engineering-access/users/{id:int}/groups", UpdateUserGroupsAsync)
            .RequireAuthorization(EngineeringPermissions.SettingsManageUsers);
        api.MapPost("/engineering-access/groups", CreateGroupAsync)
            .RequireAuthorization(EngineeringPermissions.SettingsManageGroups);
        api.MapPut("/engineering-access/groups/{id:int}", UpdateGroupAsync)
            .RequireAuthorization(EngineeringPermissions.SettingsManageGroups);
    }

    private static async Task<IResult> GetOverviewAsync(
        EngineeringRoleDbContext db,
        CancellationToken cancellationToken)
    {
        var users = await db.Users
            .AsNoTracking()
            .Include(user => user.GroupMemberships)
            .OrderByDescending(user => user.IsActive)
            .ThenBy(user => user.DisplayName)
            .ThenBy(user => user.AccountName)
            .Select(user => new EngineeringAccessUserDto(
                user.Id,
                user.AccountName,
                user.DisplayName,
                user.IsActive,
                user.LastSeenAt,
                user.GroupMemberships.Select(membership => membership.AppGroupId).OrderBy(id => id).ToList()))
            .ToListAsync(cancellationToken);
        var groups = await db.Groups
            .AsNoTracking()
            .Include(group => group.Permissions)
            .Include(group => group.UserMemberships)
            .OrderBy(group => group.Name)
            .Select(group => new EngineeringAccessGroupDto(
                group.Id,
                group.Name,
                group.Description,
                group.IsSystemGroup,
                group.Permissions
                    .Where(permission => permission.PermissionKey.StartsWith("engineering."))
                    .Select(permission => permission.PermissionKey)
                    .OrderBy(permission => permission)
                    .ToList(),
                group.UserMemberships.Count))
            .ToListAsync(cancellationToken);
        var permissions = EngineeringPermissions.All
            .Select(permission => new EngineeringPermissionDto(
                permission.Key,
                permission.Label,
                permission.Description,
                permission.Category))
            .ToList();
        return Results.Ok(new EngineeringAccessOverviewDto(users, groups, permissions));
    }

    private static async Task<IResult> UpdateUserGroupsAsync(
        int id,
        EngineeringUserGroupsUpdateDto dto,
        EngineeringRoleDbContext db,
        CancellationToken cancellationToken)
    {
        var user = await db.Users
            .Include(candidate => candidate.GroupMemberships)
            .SingleOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);
        if (user is null) return Results.NotFound();

        var groupIds = dto.GroupIds.Distinct().OrderBy(groupId => groupId).ToArray();
        if (groupIds.Length > 0 &&
            await db.Groups.CountAsync(group => groupIds.Contains(group.Id), cancellationToken) != groupIds.Length)
        {
            return Results.BadRequest(new ErrorDto("InvalidGroup", "One or more selected Engineering groups no longer exist."));
        }

        db.UserGroupMemberships.RemoveRange(user.GroupMemberships.Where(membership => !groupIds.Contains(membership.AppGroupId)));
        foreach (var groupId in groupIds.Where(groupId => user.GroupMemberships.All(membership => membership.AppGroupId != groupId)))
        {
            user.GroupMemberships.Add(new EngineeringUserGroupMembershipRecord { AppGroupId = groupId });
        }

        return await SaveWithAdministratorGuardAsync(db, cancellationToken)
            ? Results.Ok()
            : Results.Conflict(new ErrorDto(
                "LastAccessAdministrator",
                "At least one active user must retain both Engineering user-management and group-management permissions."));
    }

    private static async Task<IResult> CreateGroupAsync(
        EngineeringGroupUpsertDto dto,
        EngineeringRoleDbContext db,
        CancellationToken cancellationToken)
    {
        var name = dto.Name.Trim();
        if (string.IsNullOrWhiteSpace(name))
            return Results.BadRequest(new ErrorDto("GroupNameRequired", "Group name is required."));
        if (await db.Groups.AnyAsync(group => group.Name.ToUpper() == name.ToUpper(), cancellationToken))
            return Results.Conflict(new ErrorDto("DuplicateGroup", "An Engineering group with that name already exists."));

        var group = new EngineeringAccessGroupRecord
        {
            Name = name,
            Description = Clean(dto.Description),
            IsSystemGroup = false
        };
        foreach (var permission in NormalizePermissions(dto.Permissions))
            group.Permissions.Add(new EngineeringGroupPermissionRecord { PermissionKey = permission });
        db.Groups.Add(group);
        await db.SaveChangesAsync(cancellationToken);
        return Results.Created($"/api/engineering-access/groups/{group.Id}", new { group.Id });
    }

    private static async Task<IResult> UpdateGroupAsync(
        int id,
        EngineeringGroupUpsertDto dto,
        EngineeringRoleDbContext db,
        CancellationToken cancellationToken)
    {
        var group = await db.Groups
            .Include(candidate => candidate.Permissions)
            .SingleOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);
        if (group is null) return Results.NotFound();

        var name = dto.Name.Trim();
        if (string.IsNullOrWhiteSpace(name))
            return Results.BadRequest(new ErrorDto("GroupNameRequired", "Group name is required."));
        if (await db.Groups.AnyAsync(candidate => candidate.Id != id && candidate.Name.ToUpper() == name.ToUpper(), cancellationToken))
            return Results.Conflict(new ErrorDto("DuplicateGroup", "Another Engineering group already uses that name."));

        if (!group.IsSystemGroup)
        {
            group.Name = name;
            group.Description = Clean(dto.Description);
        }
        group.UpdatedAt = DateTimeOffset.UtcNow;
        var permissions = NormalizePermissions(dto.Permissions);
        db.GroupPermissions.RemoveRange(group.Permissions.Where(permission => !permissions.Contains(permission.PermissionKey)));
        foreach (var permission in permissions.Where(permission => group.Permissions.All(existing => existing.PermissionKey != permission)))
            group.Permissions.Add(new EngineeringGroupPermissionRecord { PermissionKey = permission });

        return await SaveWithAdministratorGuardAsync(db, cancellationToken)
            ? Results.Ok()
            : Results.Conflict(new ErrorDto(
                "LastAccessAdministrator",
                "At least one active user must retain both Engineering user-management and group-management permissions."));
    }

    private static async Task<bool> SaveWithAdministratorGuardAsync(
        EngineeringRoleDbContext db,
        CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        var users = await db.Users
            .Include(user => user.ModuleAccessAssignments)
            .Include(user => user.GroupMemberships)
                .ThenInclude(membership => membership.Group)
                    .ThenInclude(group => group.Permissions)
            .ToListAsync(cancellationToken);
        foreach (var user in users)
        {
            var role = EngineeringPermissions.RoleFor(user.GroupMemberships
                .SelectMany(membership => membership.Group.Permissions)
                .Select(permission => permission.PermissionKey));
            var legacyAccess = user.ModuleAccessAssignments.SingleOrDefault(access =>
                access.ModuleKey == ApplicationModules.Engineering);
            if (role is null)
            {
                if (legacyAccess is not null) db.UserModuleAccess.Remove(legacyAccess);
                continue;
            }
            if (legacyAccess is null)
            {
                user.ModuleAccessAssignments.Add(new EngineeringModuleAccessRecord
                {
                    ModuleKey = ApplicationModules.Engineering,
                    Role = role,
                    UpdatedAt = DateTimeOffset.UtcNow
                });
            }
            else
            {
                legacyAccess.Role = role;
                legacyAccess.UpdatedAt = DateTimeOffset.UtcNow;
            }
        }
        await db.SaveChangesAsync(cancellationToken);
        var administrators = await db.Users
            .AsNoTracking()
            .Where(user => user.IsActive)
            .Select(user => user.GroupMemberships
                .SelectMany(membership => membership.Group.Permissions)
                .Select(permission => permission.PermissionKey)
                .ToList())
            .ToListAsync(cancellationToken);
        var valid = administrators.Any(permissions =>
            permissions.Contains(EngineeringPermissions.SettingsManageUsers, StringComparer.OrdinalIgnoreCase)
            && permissions.Contains(EngineeringPermissions.SettingsManageGroups, StringComparer.OrdinalIgnoreCase));
        if (!valid)
        {
            await transaction.RollbackAsync(cancellationToken);
            return false;
        }

        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    private static HashSet<string> NormalizePermissions(IEnumerable<string> permissions) =>
        EngineeringPermissions.Expand(permissions);

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
