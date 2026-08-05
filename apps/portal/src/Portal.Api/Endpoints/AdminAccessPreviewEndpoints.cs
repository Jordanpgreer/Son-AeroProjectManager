using Microsoft.EntityFrameworkCore;
using Portal.Api.Data;
using Portal.Api.Dtos;
using Portal.Api.Models;
using Portal.Api.Services;
using SonAero.Platform.Security;

namespace Portal.Api.Endpoints;

public static class AdminAccessPreviewEndpoints
{
    private static readonly HashSet<string> PreviewableApplications =
    [
        AccessPreviewApplications.ProjectTracker,
        AccessPreviewApplications.Engineering,
        AccessPreviewApplications.Estimating
    ];

    public static void MapAdminAccessPreviewEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/admin/access-previews", GetOverviewAsync).RequireAuthorization();
        api.MapPost("/admin/access-previews/{targetKey}/launch/{applicationId}", IssueLaunchAsync)
            .RequireAuthorization();
    }

    private static async Task<IResult> GetOverviewAsync(
        PortalUserService users,
        PortalRoleDbContext db,
        ApplicationRegistry registry,
        CancellationToken cancellationToken)
    {
        var currentUser = await users.CurrentAsync(cancellationToken);
        if (!IsAdmin(currentUser.Role)) return AdministratorRequired();

        var userRecords = await db.Users
            .AsNoTracking()
            .Where(user => user.IsActive)
            .Include(user => user.ModuleAccessAssignments)
            .Include(user => user.ProjectTrackerGroupMemberships)
                .ThenInclude(membership => membership.Group)
                    .ThenInclude(group => group.Permissions)
            .Include(user => user.EngineeringGroupMemberships)
                .ThenInclude(membership => membership.Group)
                    .ThenInclude(group => group.Permissions)
            .OrderBy(user => user.DisplayName)
            .ThenBy(user => user.AccountName)
            .ToListAsync(cancellationToken);

        var userTargets = userRecords
            .Where(user => !WindowsAccountNames.Equals(user.AccountName, currentUser.AccountName))
            .Select(user => ToUserTarget(user, registry))
            .ToList();

        var projectGroups = await db.ProjectTrackerGroups
            .AsNoTracking()
            .Include(group => group.Permissions)
            .OrderBy(group => group.Name)
            .ToListAsync(cancellationToken);
        var engineeringGroups = await db.EngineeringGroups
            .AsNoTracking()
            .Include(group => group.Permissions)
            .OrderBy(group => group.Name)
            .ToListAsync(cancellationToken);

        var groupTargets = projectGroups
            .Select(group => ToProjectTrackerGroupTarget(group, registry))
            .Concat(engineeringGroups.Select(group => ToEngineeringGroupTarget(group, registry)))
            .OrderBy(group => group.Title, StringComparer.OrdinalIgnoreCase)
            .ThenBy(group => group.Role, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return Results.Ok(new AdminAccessPreviewOverviewDto(userTargets, groupTargets));
    }

    private static async Task<IResult> IssueLaunchAsync(
        string targetKey,
        string applicationId,
        PortalUserService users,
        PortalRoleDbContext db,
        ApplicationRegistry registry,
        CancellationToken cancellationToken)
    {
        var currentUser = await users.CurrentAsync(cancellationToken);
        if (!IsAdmin(currentUser.Role)) return AdministratorRequired();

        var target = await ResolveTargetAsync(targetKey, db, registry, cancellationToken);
        if (target is null)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status404NotFound,
                title: "Preview target unavailable",
                detail: "The selected user or group no longer exists or is inactive.");
        }

        var application = target.Applications.SingleOrDefault(candidate =>
            string.Equals(candidate.Id, applicationId, StringComparison.OrdinalIgnoreCase)
            && candidate.Status == ApplicationStatus.Active);
        if (application is null || !PreviewableApplications.Contains(application.Id))
        {
            return Results.Problem(
                statusCode: StatusCodes.Status403Forbidden,
                title: "Application unavailable",
                detail: "The selected target does not currently have access to that application.");
        }

        var registryEntry = registry.All.SingleOrDefault(candidate =>
            string.Equals(candidate.Id, application.Id, StringComparison.OrdinalIgnoreCase)
            && candidate.Status == ApplicationStatus.Active);
        if (registryEntry is null || !Uri.TryCreate(registryEntry.Url, UriKind.Absolute, out var applicationUri))
        {
            return Results.Problem(
                statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "Application launch unavailable",
                detail: "The application does not have a valid server URL configured.");
        }

        await EnsureAccessPreviewTableAsync(db, cancellationToken);
        var now = DateTimeOffset.UtcNow;
        await db.AccessPreviewSessions
            .Where(session => session.SessionExpiresAt <= now || session.RevokedAt != null)
            .ExecuteDeleteAsync(cancellationToken);

        var rawToken = AccessPreviewTokens.Create();
        var session = new AccessPreviewSessionRecord
        {
            Id = Guid.NewGuid(),
            TokenHash = AccessPreviewTokens.Hash(rawToken),
            AdministratorAccountName = currentUser.AccountName,
            TargetKey = target.Key,
            ApplicationId = application.Id,
            IssuedAt = now,
            LaunchExpiresAt = now.AddMinutes(2),
            SessionExpiresAt = now.AddMinutes(30)
        };
        db.AccessPreviewSessions.Add(session);
        await db.SaveChangesAsync(cancellationToken);

        var startUri = new UriBuilder(applicationUri)
        {
            Path = "/access-preview/start",
            Query = string.Empty,
            Fragment = string.Empty
        }.Uri.ToString();
        return Results.Ok(new AdminAccessPreviewLaunchDto(startUri, rawToken, session.LaunchExpiresAt));
    }

    private static async Task<AdminAccessPreviewTargetDto?> ResolveTargetAsync(
        string targetKey,
        PortalRoleDbContext db,
        ApplicationRegistry registry,
        CancellationToken cancellationToken)
    {
        if (!AccessPreviewTarget.TryParse(targetKey, out var target)) return null;

        if (target.Kind == AccessPreviewTargetKinds.User)
        {
            var user = await db.Users
                .AsNoTracking()
                .Where(candidate => candidate.Id == target.Id && candidate.IsActive)
                .Include(candidate => candidate.ModuleAccessAssignments)
                .Include(candidate => candidate.ProjectTrackerGroupMemberships)
                    .ThenInclude(membership => membership.Group)
                        .ThenInclude(group => group.Permissions)
                .Include(candidate => candidate.EngineeringGroupMemberships)
                    .ThenInclude(membership => membership.Group)
                        .ThenInclude(group => group.Permissions)
                .SingleOrDefaultAsync(cancellationToken);
            return user is null ? null : ToUserTarget(user, registry);
        }

        if (target.Kind == AccessPreviewTargetKinds.ProjectTrackerGroup)
        {
            var group = await db.ProjectTrackerGroups
                .AsNoTracking()
                .Include(candidate => candidate.Permissions)
                .SingleOrDefaultAsync(candidate => candidate.Id == target.Id, cancellationToken);
            return group is null ? null : ToProjectTrackerGroupTarget(group, registry);
        }

        if (target.Kind == AccessPreviewTargetKinds.EngineeringGroup)
        {
            var group = await db.EngineeringGroups
                .AsNoTracking()
                .Include(candidate => candidate.Permissions)
                .SingleOrDefaultAsync(candidate => candidate.Id == target.Id, cancellationToken);
            return group is null ? null : ToEngineeringGroupTarget(group, registry);
        }

        return null;
    }

    private static AdminAccessPreviewTargetDto ToUserTarget(
        PortalRoleRecord user,
        ApplicationRegistry registry)
    {
        var visibleIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var projectPermissions = user.ProjectTrackerGroupMemberships
            .SelectMany(membership => membership.Group.Permissions)
            .Select(permission => permission.PermissionKey)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (projectPermissions.Contains(ApplicationPermissions.ModuleView))
            visibleIds.Add(AccessPreviewApplications.ProjectTracker);

        var enabledModules = user.ModuleAccessAssignments
            .Where(access => ApplicationModules.Normalize(access.ModuleKey) is not null)
            .Select(access => ApplicationModules.Normalize(access.ModuleKey)!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var engineeringPermissions = user.EngineeringGroupMemberships
            .SelectMany(membership => membership.Group.Permissions)
            .Select(permission => permission.PermissionKey)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (enabledModules.Contains(ApplicationModules.Engineering)
            && engineeringPermissions.Contains(EngineeringPermissions.ModuleView))
            visibleIds.Add(AccessPreviewApplications.Engineering);
        if (enabledModules.Contains(ApplicationModules.Estimating))
            visibleIds.Add(AccessPreviewApplications.Estimating);

        var role = ApplicationRoles.Normalize(user.Role) ?? ApplicationRoles.Viewer;
        var applications = registry.All
            .Where(application => visibleIds.Contains(application.Id)
                && ApplicationRegistry.IsVisibleTo(application, role))
            .OrderBy(application => application.Order)
            .Select(ToApplicationDto)
            .ToList();
        return new AdminAccessPreviewTargetDto(
            $"user:{user.Id}",
            AccessPreviewTargetKinds.User,
            string.IsNullOrWhiteSpace(user.DisplayName) ? user.AccountName : user.DisplayName,
            user.AccountName,
            role,
            applications);
    }

    private static AdminAccessPreviewTargetDto ToProjectTrackerGroupTarget(
        PortalProjectTrackerGroupRecord group,
        ApplicationRegistry registry)
    {
        var canView = group.Permissions.Any(permission =>
            string.Equals(permission.PermissionKey, ApplicationPermissions.ModuleView, StringComparison.OrdinalIgnoreCase));
        return new AdminAccessPreviewTargetDto(
            $"{AccessPreviewTargetKinds.ProjectTrackerGroup}:{group.Id}",
            "group",
            group.Name,
            group.Description ?? "Project Tracker permission group",
            "Project Tracker group",
            canView ? SingleApplication(registry, AccessPreviewApplications.ProjectTracker) : []);
    }

    private static AdminAccessPreviewTargetDto ToEngineeringGroupTarget(
        PortalEngineeringGroupRecord group,
        ApplicationRegistry registry)
    {
        var canView = group.Permissions.Any(permission =>
            string.Equals(permission.PermissionKey, EngineeringPermissions.ModuleView, StringComparison.OrdinalIgnoreCase));
        return new AdminAccessPreviewTargetDto(
            $"{AccessPreviewTargetKinds.EngineeringGroup}:{group.Id}",
            "group",
            group.Name,
            group.Description ?? "Engineering permission group",
            "Engineering group",
            canView ? SingleApplication(registry, AccessPreviewApplications.Engineering) : []);
    }

    private static IReadOnlyList<ApplicationDto> SingleApplication(ApplicationRegistry registry, string id)
    {
        var application = registry.All.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, id, StringComparison.OrdinalIgnoreCase)
            && candidate.Status == ApplicationStatus.Active);
        return application is null ? [] : [ToApplicationDto(application)];
    }

    private static bool IsAdmin(string role) =>
        string.Equals(role, ApplicationRoles.Admin, StringComparison.OrdinalIgnoreCase);

    private static IResult AdministratorRequired() => Results.Problem(
        statusCode: StatusCodes.Status403Forbidden,
        title: "Administrator access required",
        detail: "Only Hub administrators can preview another user or group's application access.");

    private static async Task EnsureAccessPreviewTableAsync(
        PortalRoleDbContext db,
        CancellationToken cancellationToken)
    {
        if (db.Database.IsSqlite())
        {
            await db.Database.ExecuteSqlRawAsync("""
                CREATE TABLE IF NOT EXISTS "AccessPreviewSessions" (
                    "Id" TEXT NOT NULL CONSTRAINT "PK_AccessPreviewSessions" PRIMARY KEY,
                    "TokenHash" TEXT NOT NULL,
                    "AdministratorAccountName" TEXT NOT NULL,
                    "TargetKey" TEXT NOT NULL,
                    "ApplicationId" TEXT NOT NULL,
                    "IssuedAt" TEXT NOT NULL,
                    "LaunchExpiresAt" TEXT NOT NULL,
                    "SessionExpiresAt" TEXT NOT NULL,
                    "RedeemedAt" TEXT NULL,
                    "RevokedAt" TEXT NULL
                );
                CREATE UNIQUE INDEX IF NOT EXISTS "IX_AccessPreviewSessions_TokenHash"
                    ON "AccessPreviewSessions" ("TokenHash");
                """, cancellationToken);
            return;
        }

        await db.Database.ExecuteSqlRawAsync("""
            IF OBJECT_ID(N'[AccessPreviewSessions]', N'U') IS NULL
            BEGIN
                CREATE TABLE [AccessPreviewSessions] (
                    [Id] uniqueidentifier NOT NULL CONSTRAINT [PK_AccessPreviewSessions] PRIMARY KEY,
                    [TokenHash] nvarchar(64) NOT NULL,
                    [AdministratorAccountName] nvarchar(160) NOT NULL,
                    [TargetKey] nvarchar(96) NOT NULL,
                    [ApplicationId] nvarchar(64) NOT NULL,
                    [IssuedAt] datetimeoffset NOT NULL,
                    [LaunchExpiresAt] datetimeoffset NOT NULL,
                    [SessionExpiresAt] datetimeoffset NOT NULL,
                    [RedeemedAt] datetimeoffset NULL,
                    [RevokedAt] datetimeoffset NULL
                );
                CREATE UNIQUE INDEX [IX_AccessPreviewSessions_TokenHash]
                    ON [AccessPreviewSessions] ([TokenHash]);
            END
            """, cancellationToken);
    }

    private static ApplicationDto ToApplicationDto(ApplicationEntry entry) => new(
        entry.Id,
        entry.Name,
        entry.Description,
        entry.Category,
        entry.Icon,
        entry.Url,
        entry.Order,
        entry.Status,
        !string.IsNullOrWhiteSpace(entry.PreviewPath));
}
