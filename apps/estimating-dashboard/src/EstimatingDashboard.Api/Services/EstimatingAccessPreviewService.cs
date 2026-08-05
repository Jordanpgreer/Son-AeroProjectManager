using EstimatingDashboard.Api.Auth;
using EstimatingDashboard.Api.Data;
using Microsoft.EntityFrameworkCore;
using SonAero.Platform.Security;

namespace EstimatingDashboard.Api.Services;

public sealed record EstimatingAccessPreviewStartResult(
    bool Succeeded,
    string? ErrorCode = null,
    string? ErrorMessage = null);

public sealed class EstimatingAccessPreviewService(
    EstimatingAccessDbContext db,
    IConfiguration configuration)
{
    public const string CookieName = "sonaero.estimating.access-preview";

    public async Task<EstimatingAccessPreviewStartResult> StartAsync(
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
            && candidate.ApplicationId == AccessPreviewApplications.Estimating
            && candidate.RedeemedAt == null
            && candidate.RevokedAt == null,
            cancellationToken);
        if (session is null
            || session.LaunchExpiresAt <= now
            || session.SessionExpiresAt <= now
            || !WindowsAccountNames.Equals(session.AdministratorAccountName, actor)
            || !await IsActivePortalAdministratorAsync(actor, cancellationToken))
            return Failed("InvalidAccessPreview", "The access preview request is invalid or has expired.");

        var access = await ResolveTargetAsync(session, actor, cancellationToken);
        if (access is null)
            return Failed("AccessPreviewTargetUnavailable", "The selected user no longer has active Estimating access.");

        var redeemed = await db.AccessPreviewSessions
            .Where(candidate => candidate.Id == session.Id
                && candidate.RedeemedAt == null
                && candidate.RevokedAt == null)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(candidate => candidate.RedeemedAt, now), cancellationToken);
        if (redeemed != 1)
            return Failed("InvalidAccessPreview", "The access preview request is invalid or has expired.");

        AppendCookie(context, token, session.SessionExpiresAt);
        return new EstimatingAccessPreviewStartResult(true);
    }

    public async Task<EstimatingAccessProfile?> ResolveActiveAsync(
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
            && candidate.ApplicationId == AccessPreviewApplications.Estimating
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
                && candidate.ApplicationId == AccessPreviewApplications.Estimating
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
        var configured = configuration["Portal:Url"];
        if (Uri.TryCreate(configured, UriKind.Absolute, out var portal)
            && portal.Scheme is "http" or "https")
            return new Uri(portal, "/#/admin/hub/access").ToString();

        return $"{context.Request.Scheme}://{context.Request.Host.Host}:5140/#/admin/hub/access";
    }

    private async Task<EstimatingAccessProfile?> ResolveTargetAsync(
        AccessPreviewSessionRecord session,
        string actor,
        CancellationToken cancellationToken)
    {
        if (!AccessPreviewTarget.TryParse(session.TargetKey, out var target)
            || target.Kind != AccessPreviewTargetKinds.User)
            return null;

        var record = await db.UserModuleAccess.AsNoTracking()
            .Where(access => access.AppUserId == target.Id
                && access.ModuleKey == EstimatingModule.Key
                && access.User.IsActive)
            .Select(access => new
            {
                access.AppUserId,
                access.User.AccountName,
                access.User.DisplayName,
                access.Role
            })
            .SingleOrDefaultAsync(cancellationToken);
        var role = EstimatingRoles.Normalize(record?.Role);
        if (record is null || role is null) return null;

        return new EstimatingAccessProfile(
            record.AppUserId,
            WindowsAccountNames.Normalize(record.AccountName) ?? record.AccountName,
            string.IsNullOrWhiteSpace(record.DisplayName) ? record.AccountName : record.DisplayName,
            role,
            true,
            true,
            actor,
            session.TargetKey);
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

    private static EstimatingAccessPreviewStartResult Failed(string code, string message) =>
        new(false, code, message);
}
