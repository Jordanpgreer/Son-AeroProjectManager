using EngineeringHub.Api.Data;
using Microsoft.EntityFrameworkCore;
using SonAero.Platform.Security;

namespace EngineeringHub.Api.Services;

public sealed record EngineeringAccessPreviewStartResult(
    bool Succeeded,
    string? ErrorCode = null,
    string? ErrorMessage = null);

public sealed class EngineeringAccessPreviewService(
    EngineeringRoleDbContext db,
    IConfiguration configuration)
{
    public const string CookieName = "sonaero.engineering.access-preview";

    public async Task<EngineeringAccessPreviewStartResult> StartAsync(
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
            && candidate.ApplicationId == AccessPreviewApplications.Engineering
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

        var access = await ResolveTargetAsync(session, actor, cancellationToken);
        if (access is null)
            return Failed("AccessPreviewTargetUnavailable", "The selected user or Engineering group no longer has active Engineering access.");

        var redeemed = await db.AccessPreviewSessions
            .Where(candidate => candidate.Id == session.Id
                && candidate.RedeemedAt == null
                && candidate.RevokedAt == null)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(candidate => candidate.RedeemedAt, now), cancellationToken);
        if (redeemed != 1)
            return Failed("InvalidAccessPreview", "The access preview request is invalid or has expired.");

        AppendCookie(context, token, session.SessionExpiresAt);
        return new EngineeringAccessPreviewStartResult(true);
    }

    public async Task<EngineeringModuleAccess?> ResolveActiveAsync(
        HttpContext context,
        CancellationToken cancellationToken = default)
    {
        if (!context.Request.Cookies.TryGetValue(CookieName, out var token)
            || string.IsNullOrWhiteSpace(token)
            || token.Length > 256)
            return null;

        var actor = WindowsAccountNames.Normalize(context.User.Identity?.Name);
        if (actor is null) return null;

        var now = DateTimeOffset.UtcNow;
        var tokenHash = AccessPreviewTokens.Hash(token);
        var session = await db.AccessPreviewSessions.AsNoTracking().SingleOrDefaultAsync(candidate =>
            candidate.TokenHash == tokenHash
            && candidate.ApplicationId == AccessPreviewApplications.Engineering
            && candidate.RedeemedAt != null
            && candidate.RevokedAt == null,
            cancellationToken);
        if (session is null
            || session.SessionExpiresAt <= now
            || !WindowsAccountNames.Equals(session.AdministratorAccountName, actor)
            || !await IsActivePortalAdministratorAsync(actor, cancellationToken))
            return null;

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
                && candidate.ApplicationId == AccessPreviewApplications.Engineering
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
            "Portal:Url must be a valid HTTP(S) origin when the request host is not an approved Engineering Hub host.");
    }

    private static string? RuntimePortalOrigin(HttpRequest request)
    {
        var host = request.Host.Host;
        if (string.IsNullOrWhiteSpace(host)) return null;
        if (host.Equals("engineering.hub.son4l.local", StringComparison.OrdinalIgnoreCase))
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

    private async Task<EngineeringModuleAccess?> ResolveTargetAsync(
        AccessPreviewSessionRecord session,
        string actor,
        CancellationToken cancellationToken)
    {
        if (!AccessPreviewTarget.TryParse(session.TargetKey, out var target)) return null;

        if (target.Kind == AccessPreviewTargetKinds.User)
        {
            var user = await db.Users.AsNoTracking()
                .Where(candidate => candidate.Id == target.Id && candidate.IsActive)
                .Select(candidate => new
                {
                    candidate.AccountName,
                    candidate.DisplayName,
                    Groups = candidate.GroupMemberships.Select(membership => membership.Group.Name).ToList(),
                    Permissions = candidate.GroupMemberships
                        .SelectMany(membership => membership.Group.Permissions)
                        .Where(permission => permission.PermissionKey.StartsWith("engineering."))
                        .Select(permission => permission.PermissionKey)
                        .ToList()
                })
                .SingleOrDefaultAsync(cancellationToken);
            if (user is null) return null;

            return BuildAccess(
                user.Permissions,
                user.Groups,
                WindowsAccountNames.Normalize(user.AccountName) ?? user.AccountName,
                string.IsNullOrWhiteSpace(user.DisplayName) ? user.AccountName : user.DisplayName,
                actor,
                session.TargetKey);
        }

        if (target.Kind is AccessPreviewTargetKinds.ProjectTrackerGroup
            or AccessPreviewTargetKinds.EngineeringGroup)
        {
            var group = await db.Groups.AsNoTracking()
                .Where(candidate => candidate.Id == target.Id)
                .Select(candidate => new
                {
                    candidate.Name,
                    Permissions = candidate.Permissions
                        .Where(permission => permission.PermissionKey.StartsWith("engineering."))
                        .Select(permission => permission.PermissionKey)
                        .ToList()
                })
                .SingleOrDefaultAsync(cancellationToken);
            if (group is null) return null;

            return BuildAccess(
                group.Permissions,
                [group.Name],
                null,
                group.Name,
                actor,
                session.TargetKey);
        }

        return null;
    }

    private static EngineeringModuleAccess? BuildAccess(
        IEnumerable<string> rawPermissions,
        IEnumerable<string> groups,
        string? accountName,
        string title,
        string actor,
        string targetKey)
    {
        var permissions = EngineeringPermissions.Expand(rawPermissions)
            .OrderBy(permission => permission, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var role = EngineeringPermissions.RoleFor(permissions);
        if (role is null) return null;

        return new EngineeringModuleAccess(
            role,
            true,
            permissions,
            groups.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(group => group).ToArray(),
            accountName,
            title,
            true,
            actor,
            targetKey,
            title);
    }

    private async Task<bool> IsActivePortalAdministratorAsync(
        string accountName,
        CancellationToken cancellationToken)
    {
        var lookupKeys = WindowsAccountNames.LookupKeys(accountName);
        return await db.Users.AsNoTracking().AnyAsync(candidate =>
            candidate.IsActive
            && lookupKeys.Contains(candidate.AccountName.ToUpper())
            && EF.Property<string>(candidate, "Role") == ApplicationRoles.Admin,
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

    private static EngineeringAccessPreviewStartResult Failed(string code, string message) =>
        new(false, code, message);
}
