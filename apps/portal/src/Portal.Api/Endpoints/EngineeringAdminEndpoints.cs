using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Portal.Api.Data;
using Portal.Api.Dtos;
using Portal.Api.Services;
using SonAero.Platform.Engineering;
using SonAero.Platform.Security;

namespace Portal.Api.Endpoints;

public static class EngineeringAdminEndpoints
{
    public static void MapEngineeringAdminEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/admin/engineering-access", GetOverviewAsync).RequireAuthorization();
        api.MapPut("/admin/engineering-access/users/{id:int}/groups", UpdateUserGroupsAsync).RequireAuthorization();
        api.MapPost("/admin/engineering-access/groups", CreateGroupAsync).RequireAuthorization();
        api.MapPut("/admin/engineering-access/groups/{id:int}", UpdateGroupAsync).RequireAuthorization();
        api.MapGet("/admin/engineering-storage", GetStorageAsync).RequireAuthorization();
        api.MapPut("/admin/engineering-storage", UpdateStorageAsync).RequireAuthorization();
        api.MapPost("/admin/engineering-storage/design-authorities", CreateDesignAuthorityAsync).RequireAuthorization();
    }

    private static async Task<IResult> GetStorageAsync(
        HttpContext http,
        PortalRoleDbContext db,
        IOptions<EngineeringStorageAdminOptions> options,
        CancellationToken cancellationToken)
    {
        var permissions = await CurrentPermissionsAsync(http, db, cancellationToken);
        if (!permissions.Contains(EngineeringPermissions.SettingsView)) return AccessDenied();
        return Results.Ok(await StorageOverviewAsync(
            db,
            options.Value,
            permissions.Contains(EngineeringPermissions.SettingsManageStorage),
            cancellationToken));
    }

    private static async Task<IResult> UpdateStorageAsync(
        EngineeringStorageUpdateDto dto,
        HttpContext http,
        PortalRoleDbContext db,
        IOptions<EngineeringStorageAdminOptions> options,
        CancellationToken cancellationToken)
    {
        if (!await HasPermissionAsync(http, db, EngineeringPermissions.SettingsManageStorage, cancellationToken))
            return AccessDenied();

        var setting = await db.EngineeringStorageSettings
            .SingleOrDefaultAsync(candidate => candidate.Id == EngineeringStorageSchema.SettingsId, cancellationToken);
        var currentRoot = setting?.RootPath;
        if (string.IsNullOrWhiteSpace(currentRoot) && !string.IsNullOrWhiteSpace(options.Value.RootPath))
            currentRoot = EngineeringStoragePolicy.NormalizeRoot(options.Value.RootPath, requireUncPath: false);

        string root;
        try
        {
            root = EngineeringStoragePolicy.NormalizeRoot(dto.RootPath, options.Value.RequireUncPath);
            EngineeringStoragePolicy.VerifyWritable(root);
            _ = EngineeringStoragePolicy.EnumerateAuthorities(root);
            if (!string.IsNullOrWhiteSpace(currentRoot) &&
                !string.Equals(currentRoot, root, StringComparison.OrdinalIgnoreCase) &&
                Directory.Exists(currentRoot))
            {
                foreach (var authority in EngineeringStoragePolicy.EnumerateAuthorities(currentRoot))
                    Directory.CreateDirectory(EngineeringStoragePolicy.AuthorityPath(root, authority));
            }
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return Results.BadRequest(new { detail = exception.Message });
        }

        var previous = DeserializeRoots(setting?.PreviousRootPathsJson)
            .Where(path => !string.Equals(path, root, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (!string.IsNullOrWhiteSpace(currentRoot) &&
            !string.Equals(currentRoot, root, StringComparison.OrdinalIgnoreCase) &&
            !previous.Contains(currentRoot, StringComparer.OrdinalIgnoreCase))
        {
            previous.Insert(0, currentRoot);
        }
        previous = previous.Distinct(StringComparer.OrdinalIgnoreCase).Take(8).ToList();

        setting ??= new PortalEngineeringStorageSettingRecord { Id = EngineeringStorageSchema.SettingsId };
        setting.RootPath = root;
        setting.PreviousRootPathsJson = JsonSerializer.Serialize(previous);
        setting.UpdatedAt = DateTimeOffset.UtcNow;
        setting.UpdatedBy = WindowsAccountNames.Normalize(http.User.Identity?.Name) ?? "Unknown";
        if (db.Entry(setting).State == EntityState.Detached) db.EngineeringStorageSettings.Add(setting);
        await db.SaveChangesAsync(cancellationToken);
        return Results.Ok(await StorageOverviewAsync(db, options.Value, true, cancellationToken));
    }

    private static async Task<IResult> CreateDesignAuthorityAsync(
        EngineeringDesignAuthorityCreateDto dto,
        HttpContext http,
        PortalRoleDbContext db,
        IOptions<EngineeringStorageAdminOptions> options,
        CancellationToken cancellationToken)
    {
        if (!await HasPermissionAsync(http, db, EngineeringPermissions.SettingsManageStorage, cancellationToken))
            return AccessDenied();

        try
        {
            var setting = await db.EngineeringStorageSettings.AsNoTracking()
                .SingleOrDefaultAsync(candidate => candidate.Id == EngineeringStorageSchema.SettingsId, cancellationToken);
            var root = EngineeringStoragePolicy.NormalizeRoot(
                setting?.RootPath ?? options.Value.RootPath,
                options.Value.RequireUncPath);
            EngineeringStoragePolicy.VerifyWritable(root);
            var authority = EngineeringStoragePolicy.NormalizeDesignAuthority(dto.Name);
            if (EngineeringStoragePolicy.EnumerateAuthorities(root).Contains(authority, StringComparer.OrdinalIgnoreCase))
                return Results.Conflict(new { detail = "That design authority already exists in Engineering storage." });
            Directory.CreateDirectory(EngineeringStoragePolicy.AuthorityPath(root, authority));
            return Results.Created(
                "/api/admin/engineering-storage/design-authorities",
                await StorageOverviewAsync(db, options.Value, true, cancellationToken));
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return Results.BadRequest(new { detail = exception.Message });
        }
    }

    private static async Task<EngineeringStorageOverviewDto> StorageOverviewAsync(
        PortalRoleDbContext db,
        EngineeringStorageAdminOptions options,
        bool canManageStorage,
        CancellationToken cancellationToken)
    {
        var setting = await db.EngineeringStorageSettings.AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.Id == EngineeringStorageSchema.SettingsId, cancellationToken);
        var configuredPath = setting?.RootPath ?? options.RootPath;
        var previousRoots = DeserializeRoots(setting?.PreviousRootPathsJson);
        if (string.IsNullOrWhiteSpace(configuredPath))
        {
            return new EngineeringStorageOverviewDto(
                string.Empty, false, false, false, false,
                "Set the controlled drawing root before creating drawings or design authorities.",
                [], previousRoots.Count, setting?.UpdatedAt, setting?.UpdatedBy, canManageStorage);
        }

        string root;
        try
        {
            root = EngineeringStoragePolicy.NormalizeRoot(configuredPath, options.RequireUncPath);
            EngineeringStoragePolicy.VerifyWritable(root);
            var authorities = EngineeringStoragePolicy.EnumerateAuthorities(root);
            return new EngineeringStorageOverviewDto(
                root, true, EngineeringStoragePolicy.IsUncPath(root), true, true,
                $"Storage is reachable and writable. Indexed {authorities.Count} design-authority folder{(authorities.Count == 1 ? string.Empty : "s")}.",
                authorities, previousRoots.Count, setting?.UpdatedAt, setting?.UpdatedBy, canManageStorage);
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return new EngineeringStorageOverviewDto(
                configuredPath, true, EngineeringStoragePolicy.IsUncPath(configuredPath), false, false,
                $"The configured storage root is unavailable: {exception.Message}",
                [], previousRoots.Count, setting?.UpdatedAt, setting?.UpdatedBy, canManageStorage);
        }
    }

    private static IReadOnlyList<string> DeserializeRoots(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try { return JsonSerializer.Deserialize<string[]>(json) ?? []; }
        catch (JsonException) { return []; }
    }

    private static async Task<IResult> GetOverviewAsync(
        HttpContext http,
        PortalRoleDbContext db,
        CancellationToken cancellationToken)
    {
        var currentPermissions = await CurrentPermissionsAsync(http, db, cancellationToken);
        if (!currentPermissions.Contains(EngineeringPermissions.SettingsView))
            return AccessDenied();

        var users = await db.Users
            .AsNoTracking()
            .Include(user => user.EngineeringGroupMemberships)
            .OrderByDescending(user => user.IsActive)
            .ThenBy(user => user.DisplayName)
            .ThenBy(user => user.AccountName)
            .Select(user => new EngineeringAdminUserDto(
                user.Id,
                user.AccountName,
                user.DisplayName,
                user.IsActive,
                user.LastSeenAt,
                user.EngineeringGroupMemberships
                    .Select(membership => membership.AppGroupId)
                    .OrderBy(id => id)
                    .ToList()))
            .ToListAsync(cancellationToken);
        var groups = await db.EngineeringGroups
            .AsNoTracking()
            .Include(group => group.Permissions)
            .Include(group => group.UserMemberships)
            .OrderBy(group => group.Name)
            .Select(group => new EngineeringAdminGroupDto(
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
            .Select(permission => new EngineeringAdminPermissionDto(
                permission.Key,
                permission.Label,
                permission.Description,
                permission.Category))
            .ToList();
        return Results.Ok(new EngineeringAdminOverviewDto(
            users,
            groups,
            permissions,
            currentPermissions.Contains(EngineeringPermissions.SettingsManageUsers),
            currentPermissions.Contains(EngineeringPermissions.SettingsManageGroups)));
    }

    private static async Task<IResult> UpdateUserGroupsAsync(
        int id,
        EngineeringAdminUserGroupsUpdateDto dto,
        HttpContext http,
        PortalRoleDbContext db,
        CancellationToken cancellationToken)
    {
        if (!await HasPermissionAsync(http, db, EngineeringPermissions.SettingsManageUsers, cancellationToken))
            return AccessDenied();

        var user = await db.Users
            .Include(candidate => candidate.EngineeringGroupMemberships)
            .Include(candidate => candidate.ModuleAccessAssignments)
            .SingleOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);
        if (user is null) return Results.NotFound();

        var groupIds = dto.GroupIds.Distinct().OrderBy(groupId => groupId).ToArray();
        if (groupIds.Length > 0
            && await db.EngineeringGroups.CountAsync(group => groupIds.Contains(group.Id), cancellationToken) != groupIds.Length)
        {
            return Results.BadRequest(new { detail = "One or more selected Engineering groups no longer exist." });
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        db.EngineeringUserGroupMemberships.RemoveRange(
            user.EngineeringGroupMemberships.Where(membership => !groupIds.Contains(membership.AppGroupId)));
        foreach (var groupId in groupIds.Where(groupId =>
                     user.EngineeringGroupMemberships.All(membership => membership.AppGroupId != groupId)))
        {
            user.EngineeringGroupMemberships.Add(new PortalEngineeringMembershipRecord { AppGroupId = groupId });
        }

        var permissions = groupIds.Length == 0
            ? []
            : await db.EngineeringGroupPermissions
                .Where(permission => groupIds.Contains(permission.AppGroupId))
                .Select(permission => permission.PermissionKey)
                .ToListAsync(cancellationToken);
        SyncModuleAccess(user, EngineeringPermissions.RoleFor(permissions));
        await db.SaveChangesAsync(cancellationToken);
        if (!await HasAccessAdministratorAsync(db, cancellationToken))
        {
            await transaction.RollbackAsync(cancellationToken);
            return LastAdministratorConflict();
        }

        await transaction.CommitAsync(cancellationToken);
        return Results.NoContent();
    }

    private static async Task<IResult> CreateGroupAsync(
        EngineeringAdminGroupUpsertDto dto,
        HttpContext http,
        PortalRoleDbContext db,
        CancellationToken cancellationToken)
    {
        if (!await HasPermissionAsync(http, db, EngineeringPermissions.SettingsManageGroups, cancellationToken))
            return AccessDenied();

        var name = dto.Name.Trim();
        if (string.IsNullOrWhiteSpace(name))
            return Results.BadRequest(new { detail = "Group name is required." });
        if (await db.EngineeringGroups.AnyAsync(group => group.Name.ToUpper() == name.ToUpper(), cancellationToken))
            return Results.Conflict(new { detail = "An Engineering group with that name already exists." });

        var group = new PortalEngineeringGroupRecord
        {
            Name = name,
            Description = Clean(dto.Description),
            IsSystemGroup = false
        };
        foreach (var permission in EngineeringPermissions.Expand(dto.Permissions))
            group.Permissions.Add(new PortalEngineeringPermissionRecord { PermissionKey = permission });
        db.EngineeringGroups.Add(group);
        await db.SaveChangesAsync(cancellationToken);
        return Results.Created($"/api/admin/engineering-access/groups/{group.Id}", new { group.Id });
    }

    private static async Task<IResult> UpdateGroupAsync(
        int id,
        EngineeringAdminGroupUpsertDto dto,
        HttpContext http,
        PortalRoleDbContext db,
        CancellationToken cancellationToken)
    {
        if (!await HasPermissionAsync(http, db, EngineeringPermissions.SettingsManageGroups, cancellationToken))
            return AccessDenied();

        var group = await db.EngineeringGroups
            .Include(candidate => candidate.Permissions)
            .Include(candidate => candidate.UserMemberships)
            .SingleOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);
        if (group is null) return Results.NotFound();

        var name = dto.Name.Trim();
        if (string.IsNullOrWhiteSpace(name))
            return Results.BadRequest(new { detail = "Group name is required." });
        if (await db.EngineeringGroups.AnyAsync(
                candidate => candidate.Id != id && candidate.Name.ToUpper() == name.ToUpper(),
                cancellationToken))
        {
            return Results.Conflict(new { detail = "Another Engineering group already uses that name." });
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        if (!group.IsSystemGroup)
        {
            group.Name = name;
            group.Description = Clean(dto.Description);
        }
        group.UpdatedAt = DateTimeOffset.UtcNow;
        var permissions = EngineeringPermissions.Expand(dto.Permissions);
        db.EngineeringGroupPermissions.RemoveRange(
            group.Permissions.Where(permission => !permissions.Contains(permission.PermissionKey)));
        foreach (var permission in permissions.Where(permission =>
                     group.Permissions.All(existing => existing.PermissionKey != permission)))
        {
            group.Permissions.Add(new PortalEngineeringPermissionRecord { PermissionKey = permission });
        }

        var affectedUserIds = group.UserMemberships.Select(membership => membership.AppUserId).ToArray();
        await db.SaveChangesAsync(cancellationToken);
        if (affectedUserIds.Length > 0)
            await SynchronizeModuleAccessAsync(db, affectedUserIds, cancellationToken);
        if (!await HasAccessAdministratorAsync(db, cancellationToken))
        {
            await transaction.RollbackAsync(cancellationToken);
            return LastAdministratorConflict();
        }

        await transaction.CommitAsync(cancellationToken);
        return Results.NoContent();
    }

    private static async Task<HashSet<string>> CurrentPermissionsAsync(
        HttpContext http,
        PortalRoleDbContext db,
        CancellationToken cancellationToken)
    {
        var accountName = WindowsAccountNames.Normalize(http.User.Identity?.Name);
        if (accountName is null) return [];
        var lookupKeys = WindowsAccountNames.LookupKeys(accountName);
        var permissions = await db.Users
            .AsNoTracking()
            .Where(user => user.IsActive && lookupKeys.Contains(user.AccountName.ToUpper()))
            .SelectMany(user => user.EngineeringGroupMemberships)
            .SelectMany(membership => membership.Group.Permissions)
            .Select(permission => permission.PermissionKey)
            .ToListAsync(cancellationToken);
        return EngineeringPermissions.Expand(permissions);
    }

    private static async Task<bool> HasPermissionAsync(
        HttpContext http,
        PortalRoleDbContext db,
        string permission,
        CancellationToken cancellationToken) =>
        (await CurrentPermissionsAsync(http, db, cancellationToken)).Contains(permission);

    private static async Task SynchronizeModuleAccessAsync(
        PortalRoleDbContext db,
        IReadOnlyCollection<int> userIds,
        CancellationToken cancellationToken)
    {
        var users = await db.Users
            .Include(user => user.ModuleAccessAssignments)
            .Where(user => userIds.Contains(user.Id))
            .ToListAsync(cancellationToken);
        var permissionsByUser = await db.EngineeringUserGroupMemberships
            .Where(membership => userIds.Contains(membership.AppUserId))
            .SelectMany(membership => membership.Group.Permissions.Select(permission => new
            {
                membership.AppUserId,
                permission.PermissionKey
            }))
            .ToListAsync(cancellationToken);
        foreach (var user in users)
        {
            var role = EngineeringPermissions.RoleFor(permissionsByUser
                .Where(item => item.AppUserId == user.Id)
                .Select(item => item.PermissionKey));
            SyncModuleAccess(user, role);
        }
        await db.SaveChangesAsync(cancellationToken);
    }

    private static void SyncModuleAccess(PortalRoleRecord user, string? role)
    {
        var access = user.ModuleAccessAssignments.SingleOrDefault(assignment =>
            assignment.ModuleKey == ApplicationModules.Engineering);
        if (role is null)
        {
            if (access is not null) user.ModuleAccessAssignments.Remove(access);
            return;
        }
        if (access is null)
        {
            user.ModuleAccessAssignments.Add(new PortalModuleAccessRecord
            {
                ModuleKey = ApplicationModules.Engineering,
                Role = role,
                UpdatedAt = DateTimeOffset.UtcNow
            });
            return;
        }
        access.Role = role;
        access.UpdatedAt = DateTimeOffset.UtcNow;
    }

    private static async Task<bool> HasAccessAdministratorAsync(
        PortalRoleDbContext db,
        CancellationToken cancellationToken)
    {
        var administrators = await db.Users
            .AsNoTracking()
            .Where(user => user.IsActive)
            .Select(user => user.EngineeringGroupMemberships
                .SelectMany(membership => membership.Group.Permissions)
                .Select(permission => permission.PermissionKey)
                .ToList())
            .ToListAsync(cancellationToken);
        return administrators.Any(permissions =>
            permissions.Contains(EngineeringPermissions.SettingsManageUsers, StringComparer.OrdinalIgnoreCase)
            && permissions.Contains(EngineeringPermissions.SettingsManageGroups, StringComparer.OrdinalIgnoreCase));
    }

    private static IResult AccessDenied() => Results.Problem(
        statusCode: StatusCodes.Status403Forbidden,
        title: "Engineering administration access denied",
        detail: "Your Engineering groups do not grant access to these settings.");

    private static IResult LastAdministratorConflict() => Results.Conflict(new
    {
        detail = "At least one active user must retain both Engineering user-management and group-management permissions."
    });

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
