using Microsoft.EntityFrameworkCore;
using QualityAssurance.Api.Auth;
using QualityAssurance.Api.Data;
using SonAero.Platform.Security;

namespace QualityAssurance.Api.Services;

public sealed record QualityAssuranceAccessPreviewStartResult(
    bool Succeeded,
    string? ErrorCode = null,
    string? ErrorMessage = null);

public sealed class QualityAssuranceAccessPreviewService(
    QualityAssuranceAccessDbContext db,
    IConfiguration configuration)
{
    public const string CookieName = "sonaero.quality-assurance.access-preview";

    public async Task<QualityAssuranceAccessPreviewStartResult> StartAsync(
        HttpContext context,
        string? token,
        CancellationToken cancellationToken = default)
    {
        var actor = WindowsAccountNames.Normalize(context.User.Identity?.Name);
        if (actor is null || string.IsNullOrWhiteSpace(token) || token.Length > 256)
            return Failed("InvalidAccessPreview", "The access preview request is invalid or has expired.");

        var now = DateTimeOffset.UtcNow;
        var tokenHash = AccessPreviewTokens.Hash(token);
        var session = await db.AccessPreviewSessions.AsNoTracking().SingleOrDefaultAsync(candidate =>
            candidate.TokenHash == tokenHash
            && candidate.ApplicationId == AccessPreviewApplications.QualityAssurance
            && candidate.RedeemedAt == null
            && candidate.RevokedAt == null,
            cancellationToken);
        if (session is null
            || session.LaunchExpiresAt <= now
            || session.SessionExpiresAt <= now
            || !WindowsAccountNames.Equals(session.AdministratorAccountName, actor)
            || !await IsActivePortalAdministratorAsync(actor, cancellationToken))
        {
            return Failed("InvalidAccessPreview", "The access preview request is invalid or has expired.");
        }

        if (await ResolveTargetAsync(session, actor, cancellationToken) is null)
        {
            return Failed(
                "AccessPreviewTargetUnavailable",
                "The selected user or shared group no longer has active Quality Assurance access.");
        }

        var redeemed = await db.AccessPreviewSessions
            .Where(candidate => candidate.Id == session.Id
                && candidate.RedeemedAt == null
                && candidate.RevokedAt == null)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(candidate => candidate.RedeemedAt, now), cancellationToken);
        if (redeemed != 1)
            return Failed("InvalidAccessPreview", "The access preview request is invalid or has expired.");

        AppendCookie(context, token, session.SessionExpiresAt);
        return new QualityAssuranceAccessPreviewStartResult(true);
    }

    public async Task<QualityAssuranceAccessProfile?> ResolveActiveAsync(
        HttpContext context,
        CancellationToken cancellationToken = default)
    {
        if (!context.Request.Cookies.TryGetValue(CookieName, out var token)
            || string.IsNullOrWhiteSpace(token)
            || token.Length > 256)
        {
            return null;
        }

        var actor = WindowsAccountNames.Normalize(context.User.Identity?.Name);
        if (actor is null) return null;

        var now = DateTimeOffset.UtcNow;
        var tokenHash = AccessPreviewTokens.Hash(token);
        var session = await db.AccessPreviewSessions.AsNoTracking().SingleOrDefaultAsync(candidate =>
            candidate.TokenHash == tokenHash
            && candidate.ApplicationId == AccessPreviewApplications.QualityAssurance
            && candidate.RedeemedAt != null
            && candidate.RevokedAt == null,
            cancellationToken);
        if (session is null
            || session.SessionExpiresAt <= now
            || !WindowsAccountNames.Equals(session.AdministratorAccountName, actor)
            || !await IsActivePortalAdministratorAsync(actor, cancellationToken))
        {
            return null;
        }

        return await ResolveTargetAsync(session, actor, cancellationToken);
    }

    public async Task RevokeAndClearAsync(
        HttpContext context,
        CancellationToken cancellationToken = default)
    {
        if (context.Request.Cookies.TryGetValue(CookieName, out var token)
            && !string.IsNullOrWhiteSpace(token)
            && token.Length <= 256)
        {
            var actor = WindowsAccountNames.Normalize(context.User.Identity?.Name);
            var tokenHash = AccessPreviewTokens.Hash(token);
            var session = await db.AccessPreviewSessions.SingleOrDefaultAsync(candidate =>
                candidate.TokenHash == tokenHash
                && candidate.ApplicationId == AccessPreviewApplications.QualityAssurance
                && candidate.RevokedAt == null,
                cancellationToken);
            if (session is not null && actor is not null
                && WindowsAccountNames.Equals(session.AdministratorAccountName, actor))
            {
                session.RevokedAt = DateTimeOffset.UtcNow;
                await db.SaveChangesAsync(cancellationToken);
            }
        }

        DeleteCookie(context);
    }

    public void DeleteCookie(HttpContext context) =>
        context.Response.Cookies.Delete(CookieName, new CookieOptions
        {
            HttpOnly = true,
            IsEssential = true,
            SameSite = SameSiteMode.Lax,
            Secure = context.Request.IsHttps,
            Path = "/"
        });

    public string GetReturnToAdminUrl(HttpContext context)
    {
        var runtimePortalOrigin = RuntimePortalOrigin(context.Request);
        if (runtimePortalOrigin is not null)
            return $"{runtimePortalOrigin}/#/admin/access";

        var configured = configuration["Portal:Url"];
        if (IsValidPortalOrigin(configured, out var portal))
            return new Uri(portal, "/#/admin/access").ToString();

        throw new InvalidOperationException(
            "Portal:Url must be a valid HTTP(S) origin when the request host is not an approved Quality Assurance host.");
    }

    private static string? RuntimePortalOrigin(HttpRequest request)
    {
        var host = request.Host.Host;
        if (string.IsNullOrWhiteSpace(host)) return null;
        if (host.Equals("quality.hub.son4l.local", StringComparison.OrdinalIgnoreCase))
            return "https://hub.son4l.local";
        if (host.Equals("SON-IIS2", StringComparison.OrdinalIgnoreCase))
            return request.IsHttps ? "https://SON-IIS2:6140" : "http://SON-IIS2:5140";
        var loopbackHost = CanonicalLoopbackHost(host);
        return loopbackHost is null ? null : $"http://{loopbackHost}:5140";
    }

    private static string? CanonicalLoopbackHost(string host) => host.ToLowerInvariant() switch
    {
        "localhost" => "localhost",
        "127.0.0.1" => "127.0.0.1",
        "::1" or "[::1]" => "[::1]",
        _ => null
    };

    private static bool IsValidPortalOrigin(string? value, out Uri portal)
    {
        if (Uri.TryCreate(value, UriKind.Absolute, out var candidate)
            && candidate.Scheme is "http" or "https"
            && string.IsNullOrEmpty(candidate.UserInfo)
            && candidate.AbsolutePath == "/"
            && string.IsNullOrEmpty(candidate.Query)
            && string.IsNullOrEmpty(candidate.Fragment))
        {
            portal = candidate;
            return true;
        }

        portal = null!;
        return false;
    }

    private async Task<QualityAssuranceAccessProfile?> ResolveTargetAsync(
        AccessPreviewSessionRecord session,
        string actor,
        CancellationToken cancellationToken)
    {
        if (!AccessPreviewTarget.TryParse(session.TargetKey, out var target)) return null;

        if (target.Kind == AccessPreviewTargetKinds.User)
        {
            var user = await db.Users.AsNoTracking()
                .Include(candidate => candidate.GroupMemberships)
                    .ThenInclude(membership => membership.Group)
                        .ThenInclude(group => group.Permissions)
                .SingleOrDefaultAsync(candidate => candidate.Id == target.Id && candidate.IsActive, cancellationToken);
            if (user is null) return null;

            var permissions = user.GroupMemberships
                .SelectMany(membership => membership.Group.Permissions)
                .Select(permission => permission.PermissionKey)
                .Where(IsQualityPermission)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
                .ToList();
            var groups = user.GroupMemberships
                .Where(membership => membership.Group.Permissions.Any(permission =>
                    IsQualityPermission(permission.PermissionKey)))
                .Select(membership => new QualityAssuranceAccessGroup(
                    membership.Group.Id,
                    membership.Group.Name))
                .DistinctBy(group => group.Id)
                .OrderBy(group => group.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
            return BuildAccess(
                user.Id,
                user.AccountName,
                string.IsNullOrWhiteSpace(user.DisplayName) ? user.AccountName : user.DisplayName,
                permissions,
                groups,
                actor,
                session.TargetKey);
        }

        if (target.Kind is AccessPreviewTargetKinds.ProjectTrackerGroup
            or AccessPreviewTargetKinds.EngineeringGroup)
        {
            var group = await db.Groups.AsNoTracking()
                .Include(candidate => candidate.Permissions)
                .SingleOrDefaultAsync(candidate => candidate.Id == target.Id, cancellationToken);
            if (group is null) return null;

            var permissions = group.Permissions
                .Select(permission => permission.PermissionKey)
                .Where(IsQualityPermission)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
                .ToList();
            return BuildAccess(
                -group.Id,
                group.Name,
                $"{group.Name} group",
                permissions,
                [new QualityAssuranceAccessGroup(group.Id, group.Name)],
                actor,
                session.TargetKey);
        }

        return null;
    }

    private static QualityAssuranceAccessProfile? BuildAccess(
        int userId,
        string accountName,
        string displayName,
        IReadOnlyList<string> permissions,
        IReadOnlyList<QualityAssuranceAccessGroup> groups,
        string actor,
        string targetKey)
    {
        if (!permissions.Contains(QualityAssurancePermissions.ModuleView, StringComparer.OrdinalIgnoreCase))
            return null;
        var role = ApplicationModuleCatalog.RoleForPermissions(
            ApplicationModules.QualityAssurance,
            permissions) ?? ApplicationRoles.Viewer;
        return new QualityAssuranceAccessProfile(
            userId,
            WindowsAccountNames.Normalize(accountName) ?? accountName,
            displayName,
            role,
            permissions,
            groups,
            true,
            actor,
            targetKey);
    }

    private async Task<bool> IsActivePortalAdministratorAsync(
        string accountName,
        CancellationToken cancellationToken)
    {
        var lookupKeys = WindowsAccountNames.LookupKeys(accountName);
        return await db.Users.AsNoTracking().AnyAsync(candidate =>
            candidate.IsActive
            && lookupKeys.Contains(candidate.AccountName.ToUpper())
            && candidate.PortalRole == ApplicationRoles.Admin,
            cancellationToken);
    }

    private void AppendCookie(HttpContext context, string token, DateTimeOffset expiresAt) =>
        context.Response.Cookies.Append(CookieName, token, new CookieOptions
        {
            HttpOnly = true,
            IsEssential = true,
            SameSite = SameSiteMode.Lax,
            Secure = context.Request.IsHttps,
            Path = "/",
            Expires = expiresAt
        });

    private static bool IsQualityPermission(string permission) =>
        permission.StartsWith("quality-assurance.", StringComparison.OrdinalIgnoreCase);

    private static QualityAssuranceAccessPreviewStartResult Failed(string code, string message) =>
        new(false, code, message);
}
