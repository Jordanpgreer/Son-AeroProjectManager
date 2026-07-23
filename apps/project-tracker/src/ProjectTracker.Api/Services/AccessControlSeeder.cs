using Microsoft.EntityFrameworkCore;
using ProjectTracker.Api.Auth;
using ProjectTracker.Api.Data;
using ProjectTracker.Api.Models;
using SonAero.Platform.Security;

namespace ProjectTracker.Api.Services;

public sealed class AccessControlSeeder
{
    public async Task SeedAsync(
        ProjectTrackerDbContext db,
        IConfiguration configuration,
        CancellationToken cancellationToken = default)
    {
        var groupIds = await EnsureDefaultGroupsAsync(db, cancellationToken);
        var existingUsers = await db.Users
            .Include(user => user.GroupMemberships)
            .ToDictionaryAsync(user => user.AccountName, StringComparer.OrdinalIgnoreCase, cancellationToken);

        foreach (var account in configuration.GetSection("Security:Admins").Get<string[]>() ?? [])
        {
            AddConfiguredUserIfMissing(db, existingUsers, account, groupIds[ApplicationGroups.Administrators], "Admin");
        }

        foreach (var account in configuration.GetSection("Security:Editors").Get<string[]>() ?? [])
        {
            AddConfiguredUserIfMissing(db, existingUsers, account, groupIds[ApplicationGroups.Managers], "Editor");
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    private static async Task<Dictionary<string, int>> EnsureDefaultGroupsAsync(
        ProjectTrackerDbContext db,
        CancellationToken cancellationToken)
    {
        var definitions = new (string Name, string Description, bool IsSystem, IReadOnlyList<string> Permissions)[]
        {
            (ApplicationGroups.Administrators, "Full administrative access to project tracker.", true, [.. ApplicationPermissions.DefaultAdministratorPermissions, .. ProjectTrackerPermissions.DefaultsForGroup(ApplicationGroups.Administrators)]),
            (ApplicationGroups.Managers, "Project management permissions across active programs.", true, [.. ApplicationPermissions.DefaultManagerPermissions, .. ProjectTrackerPermissions.DefaultsForGroup(ApplicationGroups.Managers)]),
            (ApplicationGroups.Engineering, "Operation and schedule maintenance for engineering users.", true, [.. ApplicationPermissions.DefaultEngineeringPermissions, .. ProjectTrackerPermissions.DefaultsForGroup(ApplicationGroups.Engineering)]),
            (ApplicationGroups.Sales, "Commercial updates for customer-facing users.", true, [.. ApplicationPermissions.DefaultSalesPermissions, .. ProjectTrackerPermissions.DefaultsForGroup(ApplicationGroups.Sales)]),
            (ProjectTrackerGroups.ViewOnly, "Read-only access to Project Tracker.", true, [ApplicationPermissions.ModuleView])
        };

        var groups = await db.Groups
            .Include(group => group.Permissions)
            .ToDictionaryAsync(group => group.Name, StringComparer.OrdinalIgnoreCase, cancellationToken);

        foreach (var definition in definitions)
        {
            if (!groups.TryGetValue(definition.Name, out var group))
            {
                group = new AppGroup
                {
                    Name = definition.Name,
                    Description = definition.Description,
                    IsSystemGroup = definition.IsSystem,
                    Permissions = definition.Permissions
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .Select(permission => new AppGroupPermission { PermissionKey = permission })
                        .ToList()
                };
                db.Groups.Add(group);
                groups[definition.Name] = group;
                continue;
            }

            group.Description = definition.Description;
            group.IsSystemGroup = definition.IsSystem;
        }

        await db.SaveChangesAsync(cancellationToken);
        return groups.ToDictionary(pair => pair.Key, pair => pair.Value.Id, StringComparer.OrdinalIgnoreCase);
    }

    private static void AddConfiguredUserIfMissing(
        ProjectTrackerDbContext db,
        IDictionary<string, AppUser> existingUsers,
        string? rawAccount,
        int groupId,
        string legacyRole)
    {
        if (string.IsNullOrWhiteSpace(rawAccount))
        {
            return;
        }

        var account = rawAccount.Trim();
        if (existingUsers.ContainsKey(account))
        {
            return;
        }

        var user = new AppUser
        {
            AccountName = account,
            DisplayName = DefaultDisplayName(account),
            IsActive = true,
            LastSeenAt = DateTimeOffset.UnixEpoch,
            GroupMemberships = [new AppUserGroupMembership { AppGroupId = groupId }]
        };
        db.Users.Add(user);
        db.SetLegacyRole(user, legacyRole);
        existingUsers[account] = user;
    }

    private static string DefaultDisplayName(string accountName)
    {
        var slashIndex = accountName.LastIndexOf('\\');
        return slashIndex >= 0 ? accountName[(slashIndex + 1)..] : accountName;
    }
}
