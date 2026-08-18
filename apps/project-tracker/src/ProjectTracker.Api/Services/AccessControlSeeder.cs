using System.Data;
using Microsoft.EntityFrameworkCore;
using ProjectTracker.Api.Auth;
using ProjectTracker.Api.Data;
using ProjectTracker.Api.Models;
using SonAero.Platform.Security;

namespace ProjectTracker.Api.Services;

public sealed class AccessControlSeeder
{
    private const string SharedModuleGroupsVersion = "shared-module-groups-v1";
    private const string QualityShippingPermissionsVersion = "quality-shipping-permissions-v1";
    private const string ProjectExternalLinksPermissionVersion = "project-external-links-permission-v1";
    private const string ArchivedDeletePermissionVersion = "project-archived-delete-permission-v1";

    public async Task SeedAsync(
        ProjectTrackerDbContext db,
        IConfiguration configuration,
        CancellationToken cancellationToken = default)
    {
        await EnsureVersionTableAsync(db, cancellationToken);
        var migrateSharedModuleAccess = !await HasVersionAsync(
            db,
            SharedModuleGroupsVersion,
            cancellationToken);
        var groupIds = await EnsureDefaultGroupsAsync(
            db,
            migrateSharedModuleAccess,
            cancellationToken);
        var addQualityShippingPermissions = !await HasVersionAsync(
            db,
            QualityShippingPermissionsVersion,
            cancellationToken);
        if (addQualityShippingPermissions)
        {
            await AddPermissionsToGroupAsync(
                db,
                groupIds[ApplicationGroups.Administrators],
                QualityAssurancePermissions.AdministratorDefaults,
                cancellationToken);
            await RecordVersionAsync(db, QualityShippingPermissionsVersion, cancellationToken);
        }
        var addProjectExternalLinksPermission = !await HasVersionAsync(
            db,
            ProjectExternalLinksPermissionVersion,
            cancellationToken);
        if (addProjectExternalLinksPermission)
        {
            await AddPermissionsToGroupAsync(
                db,
                groupIds[ApplicationGroups.Administrators],
                [ProjectTrackerPermissions.ProjectEditExternalLinks],
                cancellationToken);
            await RecordVersionAsync(db, ProjectExternalLinksPermissionVersion, cancellationToken);
        }
        var addArchivedDeletePermission = !await HasVersionAsync(
            db,
            ArchivedDeletePermissionVersion,
            cancellationToken);
        if (addArchivedDeletePermission)
        {
            await AddPermissionsToGroupAsync(
                db,
                groupIds[ApplicationGroups.Administrators],
                [ProjectTrackerPermissions.ArchivedDelete],
                cancellationToken);
            await RecordVersionAsync(db, ArchivedDeletePermissionVersion, cancellationToken);
        }
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

        if (migrateSharedModuleAccess)
        {
            await MigrateLegacyModuleAssignmentsAsync(db, cancellationToken);
            await RecordVersionAsync(db, SharedModuleGroupsVersion, cancellationToken);
        }
    }

    private static async Task AddPermissionsToGroupAsync(
        ProjectTrackerDbContext db,
        int groupId,
        IReadOnlyCollection<string> permissions,
        CancellationToken cancellationToken)
    {
        var group = await db.Groups
            .Include(candidate => candidate.Permissions)
            .SingleAsync(candidate => candidate.Id == groupId, cancellationToken);
        foreach (var permission in permissions.Where(permission => group.Permissions.All(existing =>
                     !string.Equals(existing.PermissionKey, permission, StringComparison.OrdinalIgnoreCase))))
        {
            group.Permissions.Add(new AppGroupPermission { PermissionKey = permission });
        }
        await db.SaveChangesAsync(cancellationToken);
    }

    private static async Task<Dictionary<string, int>> EnsureDefaultGroupsAsync(
        ProjectTrackerDbContext db,
        bool addSharedModuleDefaults,
        CancellationToken cancellationToken)
    {
        var estimatingPermissions = ApplicationModuleCatalog
            .PermissionsForModule(ApplicationModules.Estimating)
            .Select(permission => permission.Key)
            .ToArray();
        var qualityPermissions = ApplicationModuleCatalog
            .PermissionsForModule(ApplicationModules.QualityAssurance)
            .Select(permission => permission.Key)
            .ToArray();
        var definitions = new (string Name, string Description, bool IsSystem, IReadOnlyList<string> Permissions)[]
        {
            (ApplicationGroups.Administrators, "Full administrative access across SON-AERO modules.", true, [
                .. ApplicationPermissions.DefaultAdministratorPermissions,
                .. ProjectTrackerPermissions.DefaultsForGroup(ApplicationGroups.Administrators),
                .. EngineeringPermissions.DefaultsForGroup(ApplicationGroups.Administrators),
                .. estimatingPermissions,
                .. qualityPermissions
            ]),
            (ApplicationGroups.Managers, "Management, review, and project-control access across modules.", true, [
                .. ApplicationPermissions.DefaultManagerPermissions,
                .. ProjectTrackerPermissions.DefaultsForGroup(ApplicationGroups.Managers),
                .. EngineeringPermissions.DefaultsForGroup(ApplicationGroups.Managers),
                .. ApplicationModuleCatalog.PermissionsFor(ApplicationModules.Estimating, ApplicationRoles.Editor).Select(permission => permission.Key)
            ]),
            (ApplicationGroups.Engineering, "Engineering and project-operation access across modules.", true, [
                .. ApplicationPermissions.DefaultEngineeringPermissions,
                .. ProjectTrackerPermissions.DefaultsForGroup(ApplicationGroups.Engineering),
                .. EngineeringPermissions.DefaultsForGroup(ApplicationGroups.Engineering),
                .. ApplicationModuleCatalog.PermissionsFor(ApplicationModules.Estimating, ApplicationRoles.Viewer).Select(permission => permission.Key)
            ]),
            (ApplicationGroups.Sales, "Commercial and current-controlled-record visibility across modules.", true, [
                .. ApplicationPermissions.DefaultSalesPermissions,
                .. ProjectTrackerPermissions.DefaultsForGroup(ApplicationGroups.Sales),
                .. EngineeringPermissions.DefaultsForGroup(ApplicationGroups.Sales)
            ]),
            (ProjectTrackerGroups.ViewOnly, "Read-only access to current information across enabled modules.", true, [
                ApplicationPermissions.ModuleView,
                .. EngineeringPermissions.DefaultsForGroup(ProjectTrackerGroups.ViewOnly)
            ])
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
            if (!addSharedModuleDefaults) continue;

            foreach (var permission in definition.Permissions
                         .Where(IsModulePermission)
                         .Distinct(StringComparer.OrdinalIgnoreCase)
                         .Where(permission => group.Permissions.All(existing =>
                             !string.Equals(existing.PermissionKey, permission, StringComparison.OrdinalIgnoreCase))))
            {
                group.Permissions.Add(new AppGroupPermission { PermissionKey = permission });
            }
        }

        foreach (var group in groups.Values.Where(group =>
                     !string.Equals(group.Name, ApplicationGroups.Administrators, StringComparison.OrdinalIgnoreCase)))
        {
            foreach (var permission in group.Permissions.Where(permission =>
                         permission.PermissionKey.Equals(
                             ApplicationPermissions.ImportManage,
                             StringComparison.OrdinalIgnoreCase)
                         || permission.PermissionKey.Equals(
                             ProjectTrackerPermissions.ArchivedDelete,
                             StringComparison.OrdinalIgnoreCase)).ToList())
            {
                group.Permissions.Remove(permission);
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        return groups.ToDictionary(pair => pair.Key, pair => pair.Value.Id, StringComparer.OrdinalIgnoreCase);
    }

    private static async Task MigrateLegacyModuleAssignmentsAsync(
        ProjectTrackerDbContext db,
        CancellationToken cancellationToken)
    {
        var assignments = await db.UserModuleAccess
            .Where(access => access.Role != null)
            .Include(access => access.User)
                .ThenInclude(user => user.GroupMemberships)
                    .ThenInclude(membership => membership.Group)
                        .ThenInclude(group => group.Permissions)
            .ToListAsync(cancellationToken);
        var compatibilityGroups = new Dictionary<string, AppGroup>(StringComparer.OrdinalIgnoreCase);

        foreach (var assignment in assignments)
        {
            var moduleKey = ApplicationModules.Normalize(assignment.ModuleKey);
            var role = ApplicationModuleRoles.Normalize(assignment.Role);
            if (moduleKey is null || role is null) continue;

            var requiredPermissions = RequiredPermissions(moduleKey, role);
            var currentPermissions = assignment.User.GroupMemberships
                .SelectMany(membership => membership.Group.Permissions)
                .Select(permission => permission.PermissionKey)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (!requiredPermissions.All(currentPermissions.Contains))
            {
                var groupName = $"{ApplicationModuleCatalog.Find(moduleKey)!.Name} {role} Access";
                if (!compatibilityGroups.TryGetValue(groupName, out var group))
                {
                    group = await db.Groups
                        .Include(candidate => candidate.Permissions)
                        .SingleOrDefaultAsync(candidate => candidate.Name == groupName, cancellationToken)
                        ?? new AppGroup
                        {
                            Name = groupName,
                            Description = $"Migrated {role} access for {ApplicationModuleCatalog.Find(moduleKey)!.Name}.",
                            IsSystemGroup = false
                        };
                    if (group.Id == 0) db.Groups.Add(group);
                    foreach (var permission in requiredPermissions.Where(permission =>
                                 group.Permissions.All(existing => !string.Equals(
                                     existing.PermissionKey,
                                     permission,
                                     StringComparison.OrdinalIgnoreCase))))
                    {
                        group.Permissions.Add(new AppGroupPermission { PermissionKey = permission });
                    }
                    compatibilityGroups[groupName] = group;
                }

                if (assignment.User.GroupMemberships.All(membership => membership.Group != group))
                    assignment.User.GroupMemberships.Add(new AppUserGroupMembership { Group = group });
            }

            // Shared groups are authoritative after migration; the nullable row remains
            // only as a compatibility bridge for older deployments.
            assignment.Role = null;
            assignment.UpdatedAt = DateTimeOffset.UtcNow;
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    private static IReadOnlyList<string> RequiredPermissions(string moduleKey, string role) =>
        moduleKey == ApplicationModules.Engineering
            ? EngineeringPermissions.DefaultsForRole(role)
            : ApplicationModuleCatalog.PermissionsFor(moduleKey, role)
                .Select(permission => permission.Key)
                .ToArray();

    private static bool IsModulePermission(string permission) =>
        permission.StartsWith("engineering.", StringComparison.OrdinalIgnoreCase)
        || permission.StartsWith("estimating.", StringComparison.OrdinalIgnoreCase)
        || permission.StartsWith("quality-assurance.", StringComparison.OrdinalIgnoreCase);

    private static async Task EnsureVersionTableAsync(
        ProjectTrackerDbContext db,
        CancellationToken cancellationToken)
    {
        var sql = db.Database.IsSqlite()
            ? """
              CREATE TABLE IF NOT EXISTS "AccessControlVersions" (
                  "VersionKey" TEXT NOT NULL CONSTRAINT "PK_AccessControlVersions" PRIMARY KEY,
                  "AppliedAt" TEXT NOT NULL
              );
              """
            : """
              IF OBJECT_ID(N'[AccessControlVersions]', N'U') IS NULL
              BEGIN
                  CREATE TABLE [AccessControlVersions] (
                      [VersionKey] nvarchar(80) NOT NULL CONSTRAINT [PK_AccessControlVersions] PRIMARY KEY,
                      [AppliedAt] datetimeoffset NOT NULL
                  );
              END
              """;
        await db.Database.ExecuteSqlRawAsync(sql, cancellationToken);
    }

    private static async Task<bool> HasVersionAsync(
        ProjectTrackerDbContext db,
        string version,
        CancellationToken cancellationToken)
    {
        var connection = db.Database.GetDbConnection();
        var shouldClose = connection.State != ConnectionState.Open;
        if (shouldClose) await connection.OpenAsync(cancellationToken);
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM AccessControlVersions WHERE VersionKey = @version";
            var parameter = command.CreateParameter();
            parameter.ParameterName = "@version";
            parameter.Value = version;
            command.Parameters.Add(parameter);
            return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken)) > 0;
        }
        finally
        {
            if (shouldClose) await connection.CloseAsync();
        }
    }

    private static async Task RecordVersionAsync(
        ProjectTrackerDbContext db,
        string version,
        CancellationToken cancellationToken)
    {
        var sql = db.Database.IsSqlite()
            ? "INSERT OR IGNORE INTO AccessControlVersions (VersionKey, AppliedAt) VALUES ({0}, {1});"
            : "IF NOT EXISTS (SELECT 1 FROM AccessControlVersions WHERE VersionKey = {0}) INSERT INTO AccessControlVersions (VersionKey, AppliedAt) VALUES ({0}, {1});";
        await db.Database.ExecuteSqlRawAsync(sql, [version, DateTimeOffset.UtcNow], cancellationToken);
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

        var account = WindowsAccountNames.Normalize(rawAccount);
        if (account is null || existingUsers.Keys.Any(existing => WindowsAccountNames.Equals(existing, account)))
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
        return WindowsAccountNames.DisplayName(accountName);
    }
}
