using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using ProjectTracker.Api.Data;
using ProjectTracker.Api.Models;
using SonAero.Platform.Security;

namespace ProjectTracker.Api.Auth;

public enum AccessPreviewResolutionStatus
{
    None,
    Active,
    Invalid
}

public sealed record ProjectTrackerAccessPreview(
    Guid SessionId,
    string ActorAccountName,
    string TargetKey,
    string TargetKind,
    string TargetTitle,
    string? TargetAccountName,
    IReadOnlyList<string> Groups,
    IReadOnlyList<string> Permissions,
    DateTimeOffset SessionExpiresAt);

public sealed record AccessPreviewResolution(
    AccessPreviewResolutionStatus Status,
    ProjectTrackerAccessPreview? Preview = null);

public sealed record AccessPreviewRedemptionResult(
    bool Succeeded,
    string? SessionToken = null,
    DateTimeOffset? SessionExpiresAt = null,
    string? Error = null);

public sealed class ProjectTrackerAccessPreviewService(
    ProjectTrackerDbContext db,
    IConfiguration configuration)
{
    public const string CookieName = "SonAero.ProjectTracker.AccessPreview";
    private const string PermanentPortalOrigin = "https://hub.son4l.local";

    public bool HasPreviewCookie(HttpRequest request) => request.Cookies.ContainsKey(CookieName);

    public async Task<AccessPreviewResolution> ResolveAsync(
        ClaimsPrincipal principal,
        HttpRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!request.Cookies.TryGetValue(CookieName, out var token) || string.IsNullOrWhiteSpace(token))
        {
            return new AccessPreviewResolution(AccessPreviewResolutionStatus.None);
        }

        var actor = WindowsAccountNames.Normalize(principal.Identity?.Name);
        if (actor is null)
        {
            return new AccessPreviewResolution(AccessPreviewResolutionStatus.Invalid);
        }

        var now = DateTimeOffset.UtcNow;
        var tokenHash = AccessPreviewTokens.Hash(token);
        var session = await db.AccessPreviewSessions
            .AsNoTracking()
            .FirstOrDefaultAsync(candidate =>
                candidate.TokenHash == tokenHash
                && candidate.ApplicationId == AccessPreviewApplications.ProjectTracker,
                cancellationToken);

        if (session is null
            || session.RedeemedAt is null
            || session.RevokedAt is not null
            || session.SessionExpiresAt <= now
            || !WindowsAccountNames.Equals(session.AdministratorAccountName, actor)
            || !await IsActivePortalAdministratorAsync(actor, cancellationToken))
        {
            return new AccessPreviewResolution(AccessPreviewResolutionStatus.Invalid);
        }

        var preview = await ResolveTargetAsync(session, cancellationToken);
        return preview is null
            ? new AccessPreviewResolution(AccessPreviewResolutionStatus.Invalid)
            : new AccessPreviewResolution(AccessPreviewResolutionStatus.Active, preview);
    }

    public async Task<AccessPreviewRedemptionResult> RedeemAsync(
        ClaimsPrincipal principal,
        string token,
        CancellationToken cancellationToken = default)
    {
        var actor = WindowsAccountNames.Normalize(principal.Identity?.Name);
        if (actor is null || string.IsNullOrWhiteSpace(token))
        {
            return new AccessPreviewRedemptionResult(false, Error: "The preview launch is invalid.");
        }

        var now = DateTimeOffset.UtcNow;
        var tokenHash = AccessPreviewTokens.Hash(token);
        var session = await db.AccessPreviewSessions.AsNoTracking().FirstOrDefaultAsync(candidate =>
            candidate.TokenHash == tokenHash
            && candidate.ApplicationId == AccessPreviewApplications.ProjectTracker,
            cancellationToken);

        if (session is null
            || session.RedeemedAt is not null
            || session.RevokedAt is not null
            || session.LaunchExpiresAt <= now
            || session.SessionExpiresAt <= now
            || !WindowsAccountNames.Equals(session.AdministratorAccountName, actor)
            || !await IsActivePortalAdministratorAsync(actor, cancellationToken))
        {
            return new AccessPreviewRedemptionResult(false, Error: "The preview launch has expired or is not valid for this administrator.");
        }

        if (await ResolveTargetAsync(session, cancellationToken) is null)
        {
            return new AccessPreviewRedemptionResult(false, Error: "The selected user or group no longer has Project Tracker access.");
        }

        var sessionToken = AccessPreviewTokens.Create();
        var sessionTokenHash = AccessPreviewTokens.Hash(sessionToken);
        var redeemed = await db.AccessPreviewSessions
            .Where(candidate =>
                candidate.Id == session.Id
                && candidate.TokenHash == tokenHash
                && candidate.ApplicationId == AccessPreviewApplications.ProjectTracker
                && candidate.RedeemedAt == null
                && candidate.RevokedAt == null)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(candidate => candidate.TokenHash, sessionTokenHash)
                .SetProperty(candidate => candidate.RedeemedAt, now),
                cancellationToken);
        if (redeemed != 1)
        {
            return new AccessPreviewRedemptionResult(false, Error: "The preview launch was already used or expired.");
        }

        return new AccessPreviewRedemptionResult(true, sessionToken, session.SessionExpiresAt);
    }

    public async Task RevokeAsync(
        ClaimsPrincipal principal,
        HttpRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!request.Cookies.TryGetValue(CookieName, out var token) || string.IsNullOrWhiteSpace(token)) return;

        var actor = WindowsAccountNames.Normalize(principal.Identity?.Name);
        if (actor is null) return;

        var tokenHash = AccessPreviewTokens.Hash(token);
        var session = await db.AccessPreviewSessions.FirstOrDefaultAsync(candidate =>
            candidate.TokenHash == tokenHash
            && candidate.ApplicationId == AccessPreviewApplications.ProjectTracker,
            cancellationToken);
        if (session is null || !WindowsAccountNames.Equals(session.AdministratorAccountName, actor)) return;

        session.RevokedAt ??= DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
    }

    public void SetCookie(HttpResponse response, string token, DateTimeOffset expiresAt, bool secure)
    {
        response.Cookies.Append(CookieName, token, new CookieOptions
        {
            HttpOnly = true,
            Secure = secure,
            SameSite = SameSiteMode.Strict,
            Path = "/",
            Expires = expiresAt,
            IsEssential = true
        });
    }

    public void ClearCookie(HttpResponse response, bool secure)
    {
        response.Cookies.Delete(CookieName, new CookieOptions
        {
            HttpOnly = true,
            Secure = secure,
            SameSite = SameSiteMode.Strict,
            Path = "/",
            IsEssential = true
        });
    }

    public string HubAccessAdminUrl(HttpRequest request)
    {
        var runtimePortalOrigin = RuntimePortalOrigin(request);
        if (runtimePortalOrigin is not null)
        {
            return $"{runtimePortalOrigin}/#/admin/access";
        }

        var configuredOrigins = (configuration.GetSection("Cors:HubOrigins").Get<string[]>() ?? [])
            .Select(origin => Uri.TryCreate(origin, UriKind.Absolute, out var uri) ? uri : null)
            .OfType<Uri>()
            .Where(IsValidPortalOrigin)
            .ToList();
        var configuredOrigin = configuredOrigins.FirstOrDefault(uri =>
                uri.Scheme == Uri.UriSchemeHttps
                && uri.Host.Equals("hub.son4l.local", StringComparison.OrdinalIgnoreCase))
            ?? configuredOrigins.FirstOrDefault();
        if (configuredOrigin is not null)
        {
            return $"{configuredOrigin.GetLeftPart(UriPartial.Authority)}/#/admin/access";
        }

        throw new InvalidOperationException(
            "Cors:HubOrigins must contain a valid HTTP(S) origin when the request host is not an approved Project Tracker host.");
    }

    private static string? RuntimePortalOrigin(HttpRequest request)
    {
        var host = request.Host.Host;
        if (string.IsNullOrWhiteSpace(host)) return null;
        if (host.Equals("projects.hub.son4l.local", StringComparison.OrdinalIgnoreCase))
            return PermanentPortalOrigin;
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

    private static bool IsValidPortalOrigin(Uri uri) =>
        uri.Scheme is "http" or "https"
        && string.IsNullOrEmpty(uri.UserInfo)
        && uri.AbsolutePath == "/"
        && string.IsNullOrEmpty(uri.Query)
        && string.IsNullOrEmpty(uri.Fragment);

    private async Task<ProjectTrackerAccessPreview?> ResolveTargetAsync(
        AccessPreviewSessionRecord session,
        CancellationToken cancellationToken)
    {
        if (!AccessPreviewTarget.TryParse(session.TargetKey, out var target)) return null;

        if (target.Kind == AccessPreviewTargetKinds.User)
        {
            var user = await db.Users
                .AsNoTracking()
                .Include(candidate => candidate.GroupMemberships)
                    .ThenInclude(membership => membership.Group)
                        .ThenInclude(group => group.Permissions)
                .FirstOrDefaultAsync(candidate => candidate.Id == target.Id && candidate.IsActive, cancellationToken);
            if (user is null) return null;

            var groups = user.GroupMemberships
                .Select(membership => membership.Group.Name)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
                .ToList();
            var permissions = user.GroupMemberships
                .SelectMany(membership => membership.Group.Permissions.Select(permission => permission.PermissionKey))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (!permissions.Contains(ApplicationPermissions.ModuleView, StringComparer.OrdinalIgnoreCase)) return null;

            return new ProjectTrackerAccessPreview(
                session.Id,
                session.AdministratorAccountName,
                session.TargetKey,
                target.Kind,
                user.DisplayName,
                user.AccountName,
                groups,
                permissions,
                session.SessionExpiresAt);
        }

        if (target.Kind == AccessPreviewTargetKinds.ProjectTrackerGroup)
        {
            var group = await db.Groups
                .AsNoTracking()
                .Include(candidate => candidate.Permissions)
                .FirstOrDefaultAsync(candidate => candidate.Id == target.Id, cancellationToken);
            if (group is null) return null;

            var permissions = group.Permissions
                .Select(permission => permission.PermissionKey)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (!permissions.Contains(ApplicationPermissions.ModuleView, StringComparer.OrdinalIgnoreCase)) return null;

            return new ProjectTrackerAccessPreview(
                session.Id,
                session.AdministratorAccountName,
                session.TargetKey,
                target.Kind,
                $"{group.Name} group",
                null,
                [group.Name],
                permissions,
                session.SessionExpiresAt);
        }

        return null;
    }

    private async Task<bool> IsActivePortalAdministratorAsync(string accountName, CancellationToken cancellationToken)
    {
        var lookupKeys = WindowsAccountNames.LookupKeys(accountName);
        return await db.Users
            .AsNoTracking()
            .AnyAsync(user =>
                user.IsActive
                && lookupKeys.Contains(user.AccountName.ToUpper())
                && EF.Property<string>(user, "Role") == "Admin",
                cancellationToken);
    }
}
