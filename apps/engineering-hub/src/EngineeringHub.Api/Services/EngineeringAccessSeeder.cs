using EngineeringHub.Api.Auth;
using EngineeringHub.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace EngineeringHub.Api.Services;

public sealed class EngineeringAccessSeeder(
    EngineeringRoleDbContext db,
    EngineeringAccessSchemaInitializer schema)
{
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

            if (group.Permissions.Count == 0)
            {
                foreach (var permission in EngineeringPermissions.DefaultsForGroup(group.Name))
                {
                    group.Permissions.Add(new EngineeringGroupPermissionRecord
                    {
                        PermissionKey = permission
                    });
                }
            }
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
