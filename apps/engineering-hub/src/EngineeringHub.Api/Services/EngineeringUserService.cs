using System.Security.Claims;
using EngineeringHub.Api.Auth;
using EngineeringHub.Api.Dtos;
using SonAero.Platform.Security;

namespace EngineeringHub.Api.Services;

public sealed class EngineeringUserService(
    IConfiguration configuration,
    IEngineeringRoleStore roleStore)
{
    public async Task<MeDto> CurrentAsync(
        ClaimsPrincipal principal,
        EngineeringModuleAccess? effectiveAccess = null,
        CancellationToken cancellationToken = default)
    {
        var accountName = principal.Identity?.Name;
        if (string.IsNullOrWhiteSpace(accountName))
        {
            accountName = configuration["Authentication:DevelopmentAccount"] ?? "SONAERO\\engineering.admin";
        }


        accountName = WindowsAccountNames.Normalize(accountName)
            ?? throw new UnauthorizedAccessException("A valid Windows account name is required.");

        var access = effectiveAccess ?? await ResolveAccessAsync(accountName, cancellationToken);
        if (access is null || !access.IsEnabled)
        {
            throw new UnauthorizedAccessException("No active Engineering module assignment was found.");
        }

        var effectiveAccountName = access.AccountName
            ?? (access.IsPreview ? access.PreviewTargetKey : null)
            ?? accountName;

        return new MeDto(
            effectiveAccountName,
            access.DisplayName ?? ToDisplayName(effectiveAccountName),
            access.Role,
            access.Permissions,
            access.Groups,
            access.IsPreview,
            access.PreviewActorAccountName,
            access.PreviewTargetKey,
            access.PreviewTargetTitle);
    }

    public async Task<EngineeringModuleAccess?> ResolveAccessAsync(
        string? accountName,
        CancellationToken cancellationToken = default)
    {
        var resolvedAccount = string.IsNullOrWhiteSpace(accountName)
            ? configuration["Authentication:DevelopmentAccount"] ?? "SONAERO\\engineering.admin"
            : accountName;

        resolvedAccount = WindowsAccountNames.Normalize(resolvedAccount);
        if (resolvedAccount is null)
        {
            return null;
        }

        var storedAccess = await roleStore.FindAccessAsync(resolvedAccount, cancellationToken);
        if (storedAccess is not null)
        {
            return storedAccess;
        }

        return null;
    }

    public ClaimsPrincipal AttachAccess(ClaimsPrincipal principal, EngineeringModuleAccess access)
    {
        var claims = principal.Claims.ToList();
        claims.RemoveAll(claim => claim.Type == ClaimTypes.Role);
        claims.RemoveAll(claim => claim.Type == EngineeringAuthorization.PermissionClaimType);
        claims.RemoveAll(claim => claim.Type == ApplicationClaimTypes.Group);
        claims.RemoveAll(claim => claim.Type.StartsWith("sonaero.access-preview.", StringComparison.Ordinal));
        claims.Add(new Claim(ClaimTypes.Role, access.Role));
        claims.AddRange(access.Permissions
            .Select(permission => new Claim(EngineeringAuthorization.PermissionClaimType, permission)));
        claims.AddRange(access.Groups
            .Select(group => new Claim(ApplicationClaimTypes.Group, group)));
        if (access.IsPreview)
        {
            claims.Add(new Claim(AccessPreviewClaimTypes.Active, bool.TrueString));
            claims.Add(new Claim(AccessPreviewClaimTypes.ApplicationId, AccessPreviewApplications.Engineering));
            if (!string.IsNullOrWhiteSpace(access.PreviewActorAccountName))
                claims.Add(new Claim(AccessPreviewClaimTypes.ActorAccountName, access.PreviewActorAccountName));
            if (!string.IsNullOrWhiteSpace(access.PreviewTargetKey))
                claims.Add(new Claim(AccessPreviewClaimTypes.TargetKey, access.PreviewTargetKey));
            if (!string.IsNullOrWhiteSpace(access.PreviewTargetTitle))
                claims.Add(new Claim(AccessPreviewClaimTypes.TargetTitle, access.PreviewTargetTitle));
            if (!string.IsNullOrWhiteSpace(access.AccountName))
                claims.Add(new Claim(AccessPreviewClaimTypes.TargetAccountName, access.AccountName));
        }

        var identity = new ClaimsIdentity(claims, principal.Identity?.AuthenticationType, ClaimTypes.Name, ClaimTypes.Role);
        return new ClaimsPrincipal(identity);
    }

    private static string ToDisplayName(string accountName)
    {
        var name = WindowsAccountNames.DisplayName(accountName);

        name = name.Replace('.', ' ').Replace('_', ' ').Trim();
        if (name.Length == 0)
        {
            return accountName;
        }

        var words = name.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(word => char.ToUpperInvariant(word[0]) + word[1..]);
        return string.Join(' ', words);
    }
}
