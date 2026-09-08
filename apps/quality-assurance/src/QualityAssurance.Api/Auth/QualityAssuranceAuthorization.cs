using System.Security.Claims;
using SonAero.Platform.Security;

namespace QualityAssurance.Api.Auth;

public static class QualityAssurancePolicies
{
    public const string ModuleView = "QualityAssuranceModuleView";
    public const string PermissionClaim = "sonaero.permission";
    public const string GroupClaim = "sonaero.group";
    public const string AccessItem = "QualityAssuranceAccess";

    public static ClaimsPrincipal Attach(
        ClaimsPrincipal principal,
        QualityAssuranceAccessProfile access)
    {
        var claims = principal.Claims
            .Where(claim => claim.Type is not ClaimTypes.Role
                && claim.Type != PermissionClaim
                && claim.Type != GroupClaim
                && !claim.Type.StartsWith("sonaero.access-preview.", StringComparison.Ordinal))
            .ToList();
        claims.Add(new Claim(ClaimTypes.Role, access.Role));
        claims.AddRange(access.Permissions.Select(permission => new Claim(PermissionClaim, permission)));
        claims.AddRange(access.Groups.Select(group => new Claim(GroupClaim, group.Name)));
        if (access.IsPreview)
        {
            claims.Add(new Claim(AccessPreviewClaimTypes.Active, bool.TrueString));
            claims.Add(new Claim(AccessPreviewClaimTypes.ApplicationId, AccessPreviewApplications.QualityAssurance));
            if (!string.IsNullOrWhiteSpace(access.PreviewActorAccountName))
                claims.Add(new Claim(AccessPreviewClaimTypes.ActorAccountName, access.PreviewActorAccountName));
            if (!string.IsNullOrWhiteSpace(access.PreviewTargetKey))
                claims.Add(new Claim(AccessPreviewClaimTypes.TargetKey, access.PreviewTargetKey));
            claims.Add(new Claim(AccessPreviewClaimTypes.TargetTitle, access.DisplayName));
            if (access.UserId > 0)
                claims.Add(new Claim(AccessPreviewClaimTypes.TargetAccountName, access.AccountName));
        }
        var identity = new ClaimsIdentity(
            claims,
            principal.Identity?.AuthenticationType,
            ClaimTypes.Name,
            ClaimTypes.Role);
        return new ClaimsPrincipal(identity);
    }
}

public sealed record QualityAssuranceAccessProfile(
    int UserId,
    string AccountName,
    string DisplayName,
    string Role,
    IReadOnlyList<string> Permissions,
    IReadOnlyList<QualityAssuranceAccessGroup> Groups,
    bool IsPreview = false,
    string? PreviewActorAccountName = null,
    string? PreviewTargetKey = null)
{
    public bool HasPermission(string permission) => Permissions.Contains(permission, StringComparer.OrdinalIgnoreCase);
}

public sealed record QualityAssuranceAccessGroup(int Id, string Name);
