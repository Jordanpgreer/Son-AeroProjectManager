using Microsoft.EntityFrameworkCore;
using ProjectTracker.Api.Auth;
using ProjectTracker.Api.Data;
using ProjectTracker.Api.Dtos;
using ProjectTracker.Api.Models;
using ProjectTracker.Api.Services;
using SonAero.Platform.Security;

namespace ProjectTracker.Api.Endpoints;

public static class UserEndpoints
{
    public static RouteGroupBuilder MapUserEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/me", async (CurrentUserService currentUser, ProjectTrackerDbContext db, CancellationToken cancellationToken) =>
        {
            var effectiveAccountName = currentUser.EffectiveAccountName;
            if (currentUser.IsAccessPreview && effectiveAccountName is null)
            {
                var targetKey = currentUser.PreviewTargetKey ?? string.Empty;
                AccessPreviewTarget.TryParse(targetKey, out var target);
                return Results.Ok(ToUserDto(
                    targetKey,
                    currentUser.PreviewTargetTitle ?? "Project Tracker group",
                    currentUser,
                    new AccessPreviewInfoDto(
                        currentUser.ActorAccountName,
                        targetKey,
                        target.Kind,
                        currentUser.PreviewTargetTitle ?? "Project Tracker group",
                        true,
                        "/access-preview/end")));
            }

            var lookupKeys = WindowsAccountNames.LookupKeys(effectiveAccountName);
            var user = await db.Users
                .AsNoTracking()
                .FirstOrDefaultAsync(candidate => lookupKeys.Contains(candidate.AccountName.ToUpper()), cancellationToken);
            if (user is null)
            {
                return Results.Forbid();
            }

            if (user.IsActive && !currentUser.IsAccessPreview)
            {
                var tracked = await db.Users.FindAsync([user.Id], cancellationToken);
                if (tracked is not null)
                {
                    tracked.DisplayName = currentUser.DisplayName;
                    tracked.LastSeenAt = DateTimeOffset.UtcNow;
                    await db.SaveChangesAsync(cancellationToken);
                }
            }

            AccessPreviewInfoDto? preview = null;
            if (currentUser.IsAccessPreview)
            {
                var targetKey = currentUser.PreviewTargetKey ?? string.Empty;
                AccessPreviewTarget.TryParse(targetKey, out var target);
                preview = new AccessPreviewInfoDto(
                    currentUser.ActorAccountName,
                    targetKey,
                    target.Kind,
                    currentUser.PreviewTargetTitle ?? user.DisplayName,
                    true,
                    "/access-preview/end");
            }

            return Results.Ok(ToUserDto(
                effectiveAccountName ?? user.AccountName,
                user.DisplayName,
                currentUser,
                preview));
        });

        api.MapGet("/admin/access", async (ProjectTrackerDbContext db, CancellationToken cancellationToken) =>
        {
            var permissions = UnifiedPermissionDefinitions();
            var validPermissionKeys = permissions
                .Select(permission => permission.Key)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var users = await db.Users
                .AsNoTracking()
                .Include(user => user.GroupMemberships)
                .OrderByDescending(user => user.IsActive)
                .ThenBy(user => user.DisplayName)
                .ThenBy(user => user.AccountName)
                .Select(user => new RegisteredUserDto(
                    user.Id,
                    user.AccountName,
                    user.DisplayName,
                    user.IsActive,
                    user.LastSeenAt,
                    user.GroupMemberships
                        .Select(membership => membership.AppGroupId)
                        .OrderBy(id => id)
                        .ToList()))
                .ToListAsync(cancellationToken);

            var groups = await db.Groups
                .AsNoTracking()
                .Include(group => group.UserMemberships)
                .Include(group => group.Permissions)
                .OrderBy(group => group.Name)
                .Select(group => new AccessGroupDto(
                    group.Id,
                    group.Name,
                    group.Description,
                    group.IsSystemGroup,
                    group.Permissions
                        .Where(permission => validPermissionKeys.Contains(permission.PermissionKey))
                        .Select(permission => permission.PermissionKey)
                        .OrderBy(key => key)
                        .ToList(),
                    group.UserMemberships.Count))
                .ToListAsync(cancellationToken);

            return Results.Ok(new AccessOverviewDto(users, groups, permissions));
        }).RequireAuthorization(AccessOverviewAuthorization.PolicyName);

        api.MapPost("/admin/users", RegisterUserAsync).RequireAuthorization("ManageUsers");

        api.MapPut("/admin/users/{id:int}", async (
            int id,
            RegisteredUserUpsertDto dto,
            ProjectTrackerDbContext db,
            ModuleAccessService moduleAccess,
            CancellationToken cancellationToken) =>
        {
            var user = await db.Users
                .Include(candidate => candidate.GroupMemberships)
                .FirstOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);
            if (user is null)
            {
                return Results.NotFound();
            }

            var accountName = NormalizeAccountName(dto.AccountName);
            if (string.IsNullOrWhiteSpace(accountName))
            {
                return Results.BadRequest("Account name is required.");
            }

            var accountLookupKeys = WindowsAccountNames.LookupKeys(accountName);
            if (await db.Users.AnyAsync(candidate => candidate.Id != id && accountLookupKeys.Contains(candidate.AccountName.ToUpper()), cancellationToken))
            {
                return Results.Conflict("Another user is already registered with that account name.");
            }

            var groupIds = NormalizeGroupIds(dto.GroupIds);
            if (groupIds.Count > 0 && await db.Groups.CountAsync(group => groupIds.Contains(group.Id), cancellationToken) != groupIds.Count)
            {
                return Results.BadRequest("One or more selected groups no longer exist.");
            }

            if (user.IsActive && !dto.IsActive)
            {
                try
                {
                    await moduleAccess.EnsureUserCanBeDeactivatedAsync(
                        db,
                        user.Id,
                        cancellationToken);
                }
                catch (LastModuleAdministratorException exception)
                {
                    return Results.Conflict(new
                    {
                        code = "LastModuleAdministrator",
                        message = exception.Message,
                        moduleKey = exception.ModuleKey
                    });
                }
            }

            user.AccountName = accountName;
            user.DisplayName = string.IsNullOrWhiteSpace(dto.DisplayName) ? DefaultDisplayName(accountName) : dto.DisplayName.Trim();
            user.IsActive = dto.IsActive;
            ReplaceMemberships(user, groupIds);
            await SetLegacyRoleAsync(db, user, groupIds, cancellationToken);
            if (!await HasActiveAccessManagerAsync(db, cancellationToken))
            {
                return Results.BadRequest("At least one active user must retain both user-management and group-management access.");
            }

            await db.SaveChangesAsync(cancellationToken);
            return Results.Ok(await ToRegisteredUserDtoAsync(db, user.Id, cancellationToken));
        }).RequireAuthorization("ManageUsers");

        api.MapPut("/admin/users/{id:int}/groups", async (int id, UserGroupAssignmentDto dto, ProjectTrackerDbContext db, CancellationToken cancellationToken) =>
        {
            var user = await db.Users.Include(candidate => candidate.GroupMemberships).FirstOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);
            if (user is null)
            {
                return Results.NotFound();
            }

            var groupIds = NormalizeGroupIds(dto.GroupIds);
            if (groupIds.Count > 0 && await db.Groups.CountAsync(group => groupIds.Contains(group.Id), cancellationToken) != groupIds.Count)
            {
                return Results.BadRequest("One or more selected groups no longer exist.");
            }

            ReplaceMemberships(user, groupIds);
            await SetLegacyRoleAsync(db, user, groupIds, cancellationToken);
            if (!await HasActiveAccessManagerAsync(db, cancellationToken))
            {
                return Results.BadRequest("At least one active user must retain both user-management and group-management access.");
            }
            await db.SaveChangesAsync(cancellationToken);
            return Results.Ok(await ToRegisteredUserDtoAsync(db, user.Id, cancellationToken));
        }).RequireAuthorization("ManageUsers");

        api.MapPost("/admin/groups", async (AccessGroupUpsertDto dto, ProjectTrackerDbContext db, CancellationToken cancellationToken) =>
        {
            var name = dto.Name.Trim();
            if (string.IsNullOrWhiteSpace(name))
            {
                return Results.BadRequest("Group name is required.");
            }

            if (await db.Groups.AnyAsync(group => group.Name == name, cancellationToken))
            {
                return Results.Conflict("A group with that name already exists.");
            }

            var permissions = NormalizePermissions(dto.Permissions);
            if (!CanHoldAdministratorOnlyPermissions(name, permissions))
            {
                return Results.BadRequest("Workbook imports can only be assigned to the Administrators group.");
            }
            var group = new AppGroup
            {
                Name = name,
                Description = Clean(dto.Description),
                IsSystemGroup = dto.IsSystemGroup
            };
            foreach (var permission in permissions)
            {
                group.Permissions.Add(new AppGroupPermission { PermissionKey = permission });
            }

            db.Groups.Add(group);
            await db.SaveChangesAsync(cancellationToken);
            return Results.Created($"/api/admin/groups/{group.Id}", await ToAccessGroupDtoAsync(db, group.Id, cancellationToken));
        }).RequireAuthorization("ManageGroups");

        api.MapPut("/admin/groups/{id:int}", async (int id, AccessGroupUpsertDto dto, ProjectTrackerDbContext db, CancellationToken cancellationToken) =>
        {
            var group = await db.Groups
                .Include(candidate => candidate.Permissions)
                .FirstOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);
            if (group is null)
            {
                return Results.NotFound();
            }

            if (!string.Equals(group.Name, dto.Name.Trim(), StringComparison.OrdinalIgnoreCase)
                && await db.Groups.AnyAsync(candidate => candidate.Id != id && candidate.Name == dto.Name.Trim(), cancellationToken))
            {
                return Results.Conflict("Another group already uses that name.");
            }

            var name = dto.Name.Trim();
            var permissions = NormalizePermissions(dto.Permissions);
            if (!CanHoldAdministratorOnlyPermissions(name, permissions))
            {
                return Results.BadRequest("Workbook imports can only be assigned to the Administrators group.");
            }

            group.Name = name;
            group.Description = Clean(dto.Description);
            group.IsSystemGroup = dto.IsSystemGroup;
            group.UpdatedAt = DateTimeOffset.UtcNow;
            ReplacePermissions(group, permissions);
            if (!await HasActiveAccessManagerAsync(db, cancellationToken))
            {
                return Results.BadRequest("At least one active user must retain both user-management and group-management access.");
            }
            await db.SaveChangesAsync(cancellationToken);
            await SyncLegacyRolesForGroupAsync(db, group.Id, cancellationToken);
            return Results.Ok(await ToAccessGroupDtoAsync(db, group.Id, cancellationToken));
        }).RequireAuthorization("ManageGroups");

        return api;
    }

    private static UserDto ToUserDto(
        string accountName,
        string displayName,
        CurrentUserService currentUser,
        AccessPreviewInfoDto? preview)
    {
        var permissions = currentUser.Permissions;
        return new UserDto(
            accountName,
            displayName,
            currentUser.IsRegistered,
            currentUser.IsActive,
            currentUser.Groups,
            permissions,
            permissions.Any(permission =>
                permission.StartsWith("project.edit.", StringComparison.OrdinalIgnoreCase)
                || permission.StartsWith("task.edit.", StringComparison.OrdinalIgnoreCase)
                || permission is ApplicationPermissions.ProjectCreate or ApplicationPermissions.TaskCreate or ApplicationPermissions.TaskDelete),
            currentUser.Groups.Contains(ApplicationGroups.Administrators, StringComparer.OrdinalIgnoreCase)
            || permissions.Any(permission => permission is
                ApplicationPermissions.SettingsWorkCalendarManage
                or ApplicationPermissions.SettingsHolidaysManage
                or ApplicationPermissions.SettingsWorkCentersManage
                or ApplicationPermissions.ImportManage
                or ApplicationPermissions.AccessManageUsers
                or ApplicationPermissions.AccessManageGroups
                or ApplicationPermissions.ArchivedRestore),
            preview);
    }

    public static async Task<IResult> RegisterUserAsync(
        RegisteredUserUpsertDto dto,
        ProjectTrackerDbContext db,
        CancellationToken cancellationToken)
    {
        var accountName = NormalizeAccountName(dto.AccountName);
        if (string.IsNullOrWhiteSpace(accountName))
        {
            return Results.BadRequest("Account name is required.");
        }

        var accountLookupKeys = WindowsAccountNames.LookupKeys(accountName);
        if (await db.Users.AnyAsync(
                user => accountLookupKeys.Contains(user.AccountName.ToUpper()),
                cancellationToken))
        {
            return Results.Conflict("That user is already registered.");
        }

        var groupIds = NormalizeGroupIds(dto.GroupIds);
        if (groupIds.Count > 0 &&
            await db.Groups.CountAsync(group => groupIds.Contains(group.Id), cancellationToken) != groupIds.Count)
        {
            return Results.BadRequest("One or more selected groups no longer exist.");
        }

        var user = new AppUser
        {
            AccountName = accountName,
            DisplayName = string.IsNullOrWhiteSpace(dto.DisplayName)
                ? DefaultDisplayName(accountName)
                : dto.DisplayName.Trim(),
            IsActive = dto.IsActive,
            LastSeenAt = DateTimeOffset.UnixEpoch
        };
        foreach (var groupId in groupIds)
        {
            user.GroupMemberships.Add(new AppUserGroupMembership { AppGroupId = groupId });
        }

        db.Users.Add(user);
        await SetLegacyRoleAsync(db, user, groupIds, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return Results.Created(
            $"/api/admin/users/{user.Id}",
            await ToRegisteredUserDtoAsync(db, user.Id, cancellationToken));
    }

    private static void ReplaceMemberships(AppUser user, IReadOnlyCollection<int> groupIds)
    {
        var existing = user.GroupMemberships.Select(membership => membership.AppGroupId).ToHashSet();
        foreach (var membership in user.GroupMemberships.Where(membership => !groupIds.Contains(membership.AppGroupId)).ToList())
        {
            user.GroupMemberships.Remove(membership);
        }

        foreach (var groupId in groupIds.Where(groupId => !existing.Contains(groupId)))
        {
            user.GroupMemberships.Add(new AppUserGroupMembership { AppGroupId = groupId });
        }
    }

    private static void ReplacePermissions(AppGroup group, IReadOnlyCollection<string> permissions)
    {
        var existing = group.Permissions.Select(permission => permission.PermissionKey).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var permission in group.Permissions.Where(permission => !permissions.Contains(permission.PermissionKey, StringComparer.OrdinalIgnoreCase)).ToList())
        {
            group.Permissions.Remove(permission);
        }

        foreach (var permission in permissions.Where(permission => !existing.Contains(permission)))
        {
            group.Permissions.Add(new AppGroupPermission { PermissionKey = permission });
        }
    }

    private static async Task<RegisteredUserDto> ToRegisteredUserDtoAsync(ProjectTrackerDbContext db, int userId, CancellationToken cancellationToken)
    {
        return await db.Users
            .AsNoTracking()
            .Include(user => user.GroupMemberships)
            .Where(user => user.Id == userId)
            .Select(user => new RegisteredUserDto(
                user.Id,
                user.AccountName,
                user.DisplayName,
                user.IsActive,
                user.LastSeenAt,
                user.GroupMemberships.Select(membership => membership.AppGroupId).OrderBy(id => id).ToList()))
            .SingleAsync(cancellationToken);
    }

    private static async Task<AccessGroupDto> ToAccessGroupDtoAsync(ProjectTrackerDbContext db, int groupId, CancellationToken cancellationToken)
    {
        var validPermissionKeys = UnifiedPermissionDefinitions()
            .Select(permission => permission.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return await db.Groups
            .AsNoTracking()
            .Include(group => group.UserMemberships)
            .Include(group => group.Permissions)
            .Where(group => group.Id == groupId)
            .Select(group => new AccessGroupDto(
                group.Id,
                group.Name,
                group.Description,
                group.IsSystemGroup,
                group.Permissions
                    .Where(permission => validPermissionKeys.Contains(permission.PermissionKey))
                    .Select(permission => permission.PermissionKey)
                    .OrderBy(key => key)
                    .ToList(),
                group.UserMemberships.Count))
            .SingleAsync(cancellationToken);
    }

    private static List<int> NormalizeGroupIds(IReadOnlyList<int> groupIds) => groupIds.Distinct().OrderBy(id => id).ToList();

    private static bool CanHoldAdministratorOnlyPermissions(
        string groupName,
        IReadOnlyCollection<string> permissions) =>
        !permissions.Contains(ApplicationPermissions.ImportManage, StringComparer.OrdinalIgnoreCase)
        || string.Equals(groupName, ApplicationGroups.Administrators, StringComparison.OrdinalIgnoreCase);

    private static List<string> NormalizePermissions(IReadOnlyList<string> permissions)
    {
        var validKeys = UnifiedPermissionDefinitions()
            .Select(permission => permission.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var invalid = permissions
            .Where(permission => !validKeys.Contains(permission))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (invalid.Count > 0)
        {
            throw new BadHttpRequestException($"Unknown permission key(s): {string.Join(", ", invalid)}");
        }

        var normalized = permissions
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        normalized.UnionWith(EngineeringPermissions.Expand(
            normalized.Where(permission => permission.StartsWith("engineering.", StringComparison.OrdinalIgnoreCase))));

        var estimating = normalized
            .Where(permission => permission.StartsWith("estimating.", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (estimating.Count > 0)
        {
            normalized.Add("estimating.view");
            if (estimating.Any(permission => !string.Equals(permission, "estimating.view", StringComparison.OrdinalIgnoreCase)))
                normalized.Add("estimating.calculate");
        }

        return normalized.OrderBy(permission => permission, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static IReadOnlyList<PermissionDefinitionDto> UnifiedPermissionDefinitions()
    {
        var definitions = new List<PermissionDefinitionDto>();
        definitions.AddRange(ProjectTrackerPermissions.All.Select(permission => new PermissionDefinitionDto(
            permission.Key,
            permission.Label,
            permission.Description,
            permission.Category,
            "project-tracker",
            "Project Tracker")));
        definitions.AddRange(EngineeringPermissions.All.Select(permission => new PermissionDefinitionDto(
            permission.Key,
            permission.Label,
            permission.Description,
            permission.Category,
            ApplicationModules.Engineering,
            "Engineering")));

        foreach (var moduleKey in new[] { ApplicationModules.Estimating, ApplicationModules.QualityAssurance })
        {
            var module = ApplicationModuleCatalog.Find(moduleKey)!;
            definitions.AddRange(ApplicationModuleCatalog.PermissionsForModule(moduleKey).Select(permission =>
                new PermissionDefinitionDto(
                    permission.Key,
                    permission.Label,
                    permission.Description,
                    permission.Category,
                    module.Key,
                    module.Name)));
        }

        return definitions
            .DistinctBy(permission => permission.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static async Task SetLegacyRoleAsync(
        ProjectTrackerDbContext db,
        AppUser user,
        IReadOnlyCollection<int> groupIds,
        CancellationToken cancellationToken)
    {
        var assignedGroups = await db.Groups
            .AsNoTracking()
            .Where(group => groupIds.Contains(group.Id))
            .Select(group => new
            {
                group.Name,
                Permissions = group.Permissions.Select(permission => permission.PermissionKey).ToList()
            })
            .ToListAsync(cancellationToken);

        var role = assignedGroups.Any(group =>
                string.Equals(group.Name, ApplicationGroups.Administrators, StringComparison.OrdinalIgnoreCase))
            ? "Admin"
            : assignedGroups.SelectMany(group => group.Permissions).Any(permission =>
                !string.Equals(permission, ApplicationPermissions.ModuleView, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(permission, ProjectTrackerPermissions.ProjectActivityView, StringComparison.OrdinalIgnoreCase))
                ? "Editor"
                : "Viewer";
        db.SetLegacyRole(user, role);
    }

    private static async Task SyncLegacyRolesForGroupAsync(
        ProjectTrackerDbContext db,
        int groupId,
        CancellationToken cancellationToken)
    {
        var users = await db.Users
            .Include(user => user.GroupMemberships)
            .Where(user => user.GroupMemberships.Any(membership => membership.AppGroupId == groupId))
            .ToListAsync(cancellationToken);
        foreach (var user in users)
        {
            await SetLegacyRoleAsync(
                db,
                user,
                user.GroupMemberships.Select(membership => membership.AppGroupId).ToList(),
                cancellationToken);
        }
        await db.SaveChangesAsync(cancellationToken);
    }

    private static string NormalizeAccountName(string value) => WindowsAccountNames.Normalize(value) ?? string.Empty;

    private static string DefaultDisplayName(string accountName)
    {
        return WindowsAccountNames.DisplayName(accountName);
    }

    private static async Task<bool> HasActiveAccessManagerAsync(ProjectTrackerDbContext db, CancellationToken cancellationToken)
    {
        var groups = await db.Groups
            .Include(group => group.Permissions)
            .ToListAsync(cancellationToken);
        var groupPermissions = groups.ToDictionary(
            group => group.Id,
            group => group.Permissions.Select(permission => permission.PermissionKey).ToHashSet(StringComparer.OrdinalIgnoreCase));

        var users = await db.Users
            .Include(user => user.GroupMemberships)
            .ToListAsync(cancellationToken);

        return users
            .Where(user => user.IsActive)
            .Select(user => user.GroupMemberships
                .SelectMany(membership => groupPermissions.TryGetValue(membership.AppGroupId, out var permissions) ? permissions : [])
                .ToHashSet(StringComparer.OrdinalIgnoreCase))
            .Any(permissions =>
                permissions.Contains(ApplicationPermissions.AccessManageUsers)
                && permissions.Contains(ApplicationPermissions.AccessManageGroups));
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
