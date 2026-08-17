using Microsoft.EntityFrameworkCore;
using QualityAssurance.Api.Data;
using SonAero.Platform.Security;

namespace QualityAssurance.Api.Services;

public sealed class QualityPermissionSeeder(
    QualityAssuranceAccessDbContext db,
    ILogger<QualityPermissionSeeder> logger)
{
    public async Task SeedAsync(CancellationToken cancellationToken)
    {
        var administrators = await db.Groups
            .Include(group => group.Permissions)
            .SingleOrDefaultAsync(
                group => group.Name == ApplicationGroups.Administrators,
                cancellationToken);
        if (administrators is null)
        {
            logger.LogWarning("The shared Administrators group does not exist; Quality permissions were not seeded.");
            return;
        }

        var existing = administrators.Permissions
            .Select(permission => permission.PermissionKey)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var permission in QualityAssurancePermissions.AdministratorDefaults.Where(existing.Add))
            administrators.Permissions.Add(new QualityAssuranceGroupPermissionRecord { PermissionKey = permission });
        await db.SaveChangesAsync(cancellationToken);
    }
}
