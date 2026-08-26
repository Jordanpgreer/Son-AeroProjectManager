using EngineeringHub.Api.Auth;
using EngineeringHub.Api.Data;
using Microsoft.EntityFrameworkCore;
using SonAero.Platform.Security;

namespace EngineeringHub.Api.Services;

public sealed class EngineeringAccessSeeder(
    EngineeringRoleDbContext db,
    EngineeringAccessSchemaInitializer schema)
{
    private const string ToolingArchiveManagersMigration = "2026-08-tooling-archive-managers";

    public async Task SeedAsync(CancellationToken cancellationToken = default)
    {
        await schema.InitializeAsync(cancellationToken);
        var groups = await db.Groups
            .Include(group => group.Permissions)
            .ToListAsync(cancellationToken);

        var definitions = new (string Name, string Description)[]
        {
            ("Administrators", "Full Engineering Hub administration and controlled-record access."),
            ("Managers", "Engineering review, approval, and controlled-record visibility."),
            ("Engineering", "Engineering drawing, revision, specification, and supporting-document work."),
            ("Sales", "Current released drawing visibility without internal revision details."),
            ("View Only", "Read-only access to current controlled drawings.")
        };

        foreach (var definition in definitions)
        {
            var group = groups.FirstOrDefault(candidate =>
                string.Equals(candidate.Name, definition.Name, StringComparison.OrdinalIgnoreCase));
            var created = group is null;
            if (group is null)
            {
                group = new EngineeringAccessGroupRecord
                {
                    Name = definition.Name,
                    Description = definition.Description,
                    IsSystemGroup = true
                };
                db.Groups.Add(group);
                groups.Add(group);
            }

            // The system Administrators group is always full-access. Other existing groups remain
            // administrator-configurable so deployments do not silently regain removed permissions.
            if (!created && !string.Equals(group.Name, "Administrators", StringComparison.OrdinalIgnoreCase)) continue;

            var expectedPermissions = EngineeringPermissions.DefaultsForGroup(group.Name);
            foreach (var permission in expectedPermissions.Where(permission =>
                         group.Permissions.All(existing => !string.Equals(
                             existing.PermissionKey,
                             permission,
                             StringComparison.OrdinalIgnoreCase))))
            {
                group.Permissions.Add(new EngineeringGroupPermissionRecord
                {
                    PermissionKey = permission
                });
            }
        }

        if (!await db.AccessSeedMigrations.AnyAsync(
                migration => migration.Key == ToolingArchiveManagersMigration,
                cancellationToken))
        {
            var managers = groups.Single(group =>
                string.Equals(group.Name, "Managers", StringComparison.OrdinalIgnoreCase));
            if (managers.Permissions.All(permission => !string.Equals(
                    permission.PermissionKey,
                    EngineeringPermissions.ToolingArchiveManage,
                    StringComparison.OrdinalIgnoreCase)))
            {
                managers.Permissions.Add(new EngineeringGroupPermissionRecord
                {
                    PermissionKey = EngineeringPermissions.ToolingArchiveManage
                });
            }

            db.AccessSeedMigrations.Add(new EngineeringAccessSeedMigrationRecord
            {
                Key = ToolingArchiveManagersMigration
            });
        }

        await db.SaveChangesAsync(cancellationToken);

        var groupIds = groups.ToDictionary(group => group.Name, group => group.Id, StringComparer.OrdinalIgnoreCase);
        var legacyUsers = await db.Users
            .Include(user => user.GroupMemberships)
            .Include(user => user.ModuleAccessAssignments)
            .Where(user => user.GroupMemberships.Count == 0 && user.ModuleAccessAssignments.Any(access => access.ModuleKey == "engineering"))
            .ToListAsync(cancellationToken);
        foreach (var user in legacyUsers)
        {
            var role = user.ModuleAccessAssignments
                .First(access => access.ModuleKey == "engineering")
                .Role?.Trim().ToUpperInvariant();
            var groupName = role switch
            {
                "ADMIN" => "Administrators",
                "EDITOR" => "Engineering",
                _ => "View Only"
            };
            user.GroupMemberships.Add(new EngineeringUserGroupMembershipRecord
            {
                AppGroupId = groupIds[groupName]
            });
        }

        await db.SaveChangesAsync(cancellationToken);
    }
}
