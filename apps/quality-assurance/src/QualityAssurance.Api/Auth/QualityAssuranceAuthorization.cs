using System.Security.Claims;
using SonAero.Platform.Security;

namespace QualityAssurance.Api.Auth;

public static class QualityAssurancePermissions
{
    public const string View = "quality-assurance.view";
}

public static class QualityAssurancePolicies
{
    public const string Administrator = "QualityAssuranceAdministrator";
    public const string PermissionClaim = "sonaero.permission";
    public const string AccessItem = "QualityAssuranceAccess";

    public static ClaimsPrincipal Attach(
        ClaimsPrincipal principal,
        QualityAssuranceAccessProfile access)
    {
        var claims = principal.Claims
            .Where(claim => claim.Type is not ClaimTypes.Role && claim.Type != PermissionClaim)
            .ToList();
        claims.Add(new Claim(ClaimTypes.Role, access.Role));
        claims.Add(new Claim(PermissionClaim, QualityAssurancePermissions.View));
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
    string Role);
