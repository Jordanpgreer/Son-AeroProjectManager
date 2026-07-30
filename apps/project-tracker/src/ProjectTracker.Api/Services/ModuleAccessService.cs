using Microsoft.EntityFrameworkCore;
using ProjectTracker.Api.Data;
using ProjectTracker.Api.Models;
using SonAero.Platform.Security;

namespace ProjectTracker.Api.Services;

public sealed class ModuleAccessService
{
    public async Task BootstrapLegacyAssignmentsAsync(
        ProjectTrackerDbContext db,
        CancellationToken cancellationToken = default)
    {
        var users = await db.Users
            .Select(user => new
            {
                user.Id,
                LegacyRole = EF.Property<string>(user, "Role")
            })
            .ToListAsync(cancellationToken);
        var existing = await db.UserModuleAccess
            .Select(access => new { access.AppUserId, access.ModuleKey })
            .ToListAsync(cancellationToken);
        var existingKeys = existing
            .Select(access => AssignmentKey(access.AppUserId, access.ModuleKey))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var user in users)
        {
            var legacyRole = ApplicationRoles.Normalize(user.LegacyRole);
            AddIfMissing(
                db,
                existingKeys,
                user.Id,
                ApplicationModules.Engineering,
                legacyRole == ApplicationRoles.Admin ? ApplicationRoles.Admin : null);
            AddIfMissing(
                db,
                existingKeys,
                user.Id,
                ApplicationModules.Estimating,
                legacyRole);
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<AppUserModuleAccess> SetAsync(
        ProjectTrackerDbContext db,
        int userId,
        string moduleKey,
        bool enabled,
        string? role,
        CancellationToken cancellationToken = default)
    {
        var normalizedModule = ApplicationModules.Normalize(moduleKey)
            ?? throw new ModuleAccessValidationException($"Unknown module key '{moduleKey}'.");
        var normalizedRole = enabled
            ? ApplicationModuleRoles.Normalize(role)
                ?? throw new ModuleAccessValidationException(
                    "Role must be Viewer, Editor, or Admin when module access is enabled.")
            : null;

        var user = await db.Users
            .Include(candidate => candidate.ModuleAccessAssignments)
            .FirstOrDefaultAsync(candidate => candidate.Id == userId, cancellationToken)
            ?? throw new ModuleAccessUserNotFoundException(userId);
        var assignment = user.ModuleAccessAssignments
            .FirstOrDefault(access => access.ModuleKey == normalizedModule);

        if (assignment is not null
            && user.IsActive
            && assignment.Role == ApplicationRoles.Admin
            && normalizedRole != ApplicationRoles.Admin
            && !await HasOtherActiveAdministratorAsync(
                db,
                normalizedModule,
                userId,
                cancellationToken))
        {
            throw new LastModuleAdministratorException(normalizedModule);
        }

        if (assignment is null)
        {
            assignment = new AppUserModuleAccess
            {
                AppUserId = user.Id,
                ModuleKey = normalizedModule
            };
            user.ModuleAccessAssignments.Add(assignment);
        }

        assignment.Role = normalizedRole;
        assignment.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return assignment;
    }

    public async Task EnsureUserCanBeDeactivatedAsync(
        ProjectTrackerDbContext db,
        int userId,
        CancellationToken cancellationToken = default)
    {
        var administeredModules = await db.UserModuleAccess
            .Where(access =>
                access.AppUserId == userId
                && access.Role == ApplicationRoles.Admin)
            .Select(access => access.ModuleKey)
            .ToListAsync(cancellationToken);

        foreach (var moduleKey in administeredModules)
        {
            if (!await HasOtherActiveAdministratorAsync(
                    db,
                    moduleKey,
                    userId,
                    cancellationToken))
            {
                throw new LastModuleAdministratorException(moduleKey);
            }
        }
    }

    private static void AddIfMissing(
        ProjectTrackerDbContext db,
        ISet<string> existing,
        int userId,
        string moduleKey,
        string? role)
    {
        if (!existing.Add(AssignmentKey(userId, moduleKey)))
        {
            return;
        }

        db.UserModuleAccess.Add(new AppUserModuleAccess
        {
            AppUserId = userId,
            ModuleKey = moduleKey,
            Role = role
        });
    }

    private static string AssignmentKey(int userId, string moduleKey) =>
        $"{userId}:{moduleKey}";

    private static Task<bool> HasOtherActiveAdministratorAsync(
        ProjectTrackerDbContext db,
        string moduleKey,
        int excludedUserId,
        CancellationToken cancellationToken) =>
        db.UserModuleAccess.AnyAsync(
            access =>
                access.ModuleKey == moduleKey
                && access.Role == ApplicationRoles.Admin
                && access.AppUserId != excludedUserId
                && access.User.IsActive,
            cancellationToken);
}

public sealed class ModuleAccessValidationException(string message) : Exception(message);

public sealed class ModuleAccessUserNotFoundException(int userId)
    : Exception($"User {userId} was not found.")
{
    public int UserId { get; } = userId;
}

public sealed class LastModuleAdministratorException(string moduleKey)
    : Exception($"At least one active administrator must retain access to the {moduleKey} module.")
{
    public string ModuleKey { get; } = moduleKey;
}
