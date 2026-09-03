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
        api.MapPost("/admin/access-previews/{targetKey}/walkthrough", IssueWalkthroughLaunchAsync)
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
            .OrderBy(user => user.DisplayName)
            .ThenBy(user => user.AccountName)
            .ToListAsync(cancellationToken);

        var userTargets = UserTargetsForOverview(userRecords, registry, currentUser.AccountName);

        var projectGroups = await db.ProjectTrackerGroups
            .AsNoTracking()
            .Include(group => group.Permissions)
            .OrderBy(group => group.Name)
            .ToListAsync(cancellationToken);
        var groupTargets = projectGroups
            .Select(group => ToSharedGroupTarget(group, registry))
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
        CancellationToken cancellationToken) =>
        await IssueLaunchCoreAsync(
            targetKey,
            applicationId,
            walkthrough: false,
            users,
            db,
            registry,
            cancellationToken);

    private static async Task<IResult> IssueWalkthroughLaunchAsync(
        string targetKey,
        PortalUserService users,
        PortalRoleDbContext db,
        ApplicationRegistry registry,
        CancellationToken cancellationToken) =>
        await IssueLaunchCoreAsync(
            targetKey,
            AccessPreviewApplications.ProjectTracker,
            walkthrough: true,
            users,
            db,
            registry,
            cancellationToken);

    private static async Task<IResult> IssueLaunchCoreAsync(
        string targetKey,
        string applicationId,
        bool walkthrough,
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
        var expiredSessions = (await db.AccessPreviewSessions.ToListAsync(cancellationToken))
            .Where(session => session.SessionExpiresAt <= now || session.RevokedAt != null)
            .ToList();
        db.AccessPreviewSessions.RemoveRange(expiredSessions);

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

        var startUri = BuildStartUri(applicationUri, walkthrough).ToString();
        return Results.Ok(new AdminAccessPreviewLaunchDto(startUri, rawToken, session.LaunchExpiresAt));
    }

    internal static Uri BuildStartUri(Uri applicationUri, bool walkthrough)
    {
        ArgumentNullException.ThrowIfNull(applicationUri);
        return new UriBuilder(applicationUri)
        {
            Path = "/access-preview/start",
            Query = walkthrough ? "experience=walkthrough" : string.Empty,
            Fragment = string.Empty
        }.Uri;
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
                .SingleOrDefaultAsync(cancellationToken);
            return user is null ? null : ToUserTarget(user, registry);
        }

        if (target.Kind == AccessPreviewTargetKinds.ProjectTrackerGroup)
        {
            var group = await db.ProjectTrackerGroups
                .AsNoTracking()
                .Include(candidate => candidate.Permissions)
                .SingleOrDefaultAsync(candidate => candidate.Id == target.Id, cancellationToken);
            return group is null ? null : ToSharedGroupTarget(group, registry);
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

    internal static IReadOnlyList<AdminAccessPreviewTargetDto> UserTargetsForOverview(
        IEnumerable<PortalRoleRecord> users,
        ApplicationRegistry registry,
        string currentAccountName)
    {
        var targets = new List<AdminAccessPreviewTargetDto>
        {
            new(
                "unregistered-user",
                AccessPreviewTargetKinds.User,
                "Unregistered user",
                "First-time Arda visitor",
                PortalAccountStatus.PendingSetup,
                null,
                [])
        };
        targets.AddRange(users
            .Where(user => !WindowsAccountNames.Equals(user.AccountName, currentAccountName))
            .Select(user => ToUserTarget(user, registry)));
        return targets;
    }

    internal static AdminAccessPreviewTargetDto ToUserTarget(
        PortalRoleRecord user,
        ApplicationRegistry registry)
    {
        var permissions = user.ProjectTrackerGroupMemberships
            .SelectMany(membership => membership.Group.Permissions)
            .Select(permission => permission.PermissionKey)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var assignedModules = user.ModuleAccessAssignments
            .Select(access => new
            {
                ModuleKey = ApplicationModules.Normalize(access.ModuleKey),
                Role = ApplicationModuleRoles.Normalize(access.Role)
            })
            .Where(access => access.ModuleKey is not null
                && access.Role is not null
                && ApplicationModuleCatalog.Find(access.ModuleKey)?.Roles.Any(role => role.Role == access.Role) == true)
            .Select(access => access.ModuleKey!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var configured = permissions.Contains(ApplicationPermissions.ModuleView)
            || permissions.Contains(EngineeringPermissions.ModuleView)
            || permissions.Contains("estimating.view")
            || permissions.Contains(QualityAssurancePermissions.ModuleView)
            || assignedModules.Count > 0;
        var role = configured
            ? ApplicationRoles.Normalize(user.Role) ?? ApplicationRoles.Viewer
            : null;
        var applications = configured
            ? ApplicationsForAccess(registry, permissions, assignedModules, role)
            : [];
        return new AdminAccessPreviewTargetDto(
            $"user:{user.Id}",
            AccessPreviewTargetKinds.User,
            string.IsNullOrWhiteSpace(user.DisplayName) ? user.AccountName : user.DisplayName,
            user.AccountName,
            configured ? PortalAccountStatus.Configured : PortalAccountStatus.PendingSetup,
            role,
            applications);
    }

    private static AdminAccessPreviewTargetDto ToSharedGroupTarget(
        PortalProjectTrackerGroupRecord group,
        ApplicationRegistry registry)
    {
        var permissions = group.Permissions.Select(permission => permission.PermissionKey).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var role = string.Equals(group.Name, ApplicationGroups.Administrators, StringComparison.OrdinalIgnoreCase)
            ? ApplicationRoles.Admin
            : "Shared group";
        var applications = ApplicationsForAccess(registry, permissions, role: role);
        return new AdminAccessPreviewTargetDto(
            $"{AccessPreviewTargetKinds.ProjectTrackerGroup}:{group.Id}",
            "group",
            group.Name,
            group.Description ?? "Shared permission group",
            applications.Count > 0 ? PortalAccountStatus.Configured : PortalAccountStatus.PendingSetup,
            role,
            applications);
    }

    private static AdminAccessPreviewTargetDto ToEngineeringGroupTarget(
        PortalEngineeringGroupRecord group,
        ApplicationRegistry registry)
    {
        var canView = group.Permissions.Any(permission =>
            string.Equals(permission.PermissionKey, EngineeringPermissions.ModuleView, StringComparison.OrdinalIgnoreCase));
        var applications = canView ? SingleApplication(registry, AccessPreviewApplications.Engineering) : [];
        return new AdminAccessPreviewTargetDto(
            $"{AccessPreviewTargetKinds.EngineeringGroup}:{group.Id}",
            "group",
            group.Name,
            group.Description ?? "Engineering permission group",
            applications.Count > 0 ? PortalAccountStatus.Configured : PortalAccountStatus.PendingSetup,
            "Engineering group",
            applications);
    }

    private static IReadOnlyList<ApplicationDto> SingleApplication(ApplicationRegistry registry, string id)
    {
        var application = registry.All.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, id, StringComparison.OrdinalIgnoreCase)
            && candidate.Status == ApplicationStatus.Active);
        return application is null ? [] : [ToApplicationDto(application)];
    }

    internal static IReadOnlyList<ApplicationDto> ApplicationsForAccess(
        ApplicationRegistry registry,
        IReadOnlySet<string> permissions,
        IReadOnlySet<string>? assignedModules = null,
        string? role = null)
    {
        var visibleIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (permissions.Contains(ApplicationPermissions.ModuleView))
            visibleIds.Add(AccessPreviewApplications.ProjectTracker);
        if (permissions.Contains(EngineeringPermissions.ModuleView)
            || assignedModules?.Contains(ApplicationModules.Engineering) == true)
            visibleIds.Add(AccessPreviewApplications.Engineering);
        if (permissions.Contains("estimating.view")
            || assignedModules?.Contains(ApplicationModules.Estimating) == true)
            visibleIds.Add(AccessPreviewApplications.Estimating);
        if (permissions.Contains(QualityAssurancePermissions.ModuleView)
            || assignedModules?.Contains(ApplicationModules.QualityAssurance) == true)
            visibleIds.Add(AccessPreviewApplications.QualityAssurance);
        if (IsAdmin(role ?? string.Empty))
            visibleIds.Add(ApplicationRegistry.AdminConsoleApplicationId);

        return registry.All
            .Where(application =>
                visibleIds.Contains(application.Id)
                && ApplicationRegistry.IsVisibleTo(application, role ?? ApplicationRoles.Viewer))
            .OrderBy(application => application.Order)
            .Select(ToApplicationDto)
            .ToList();
    }

    private static bool IsAdmin(string? role) =>
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
