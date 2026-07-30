using System.Security.Claims;
using EngineeringHub.Api.Auth;
using EngineeringHub.Api.Dtos;
using SonAero.Platform.Security;

namespace EngineeringHub.Api.Services;

public sealed class EngineeringUserService(
    IConfiguration configuration,
    IEngineeringRoleStore roleStore)
{
    public async Task<MeDto> CurrentAsync(ClaimsPrincipal principal, CancellationToken cancellationToken = default)
    {
        var accountName = principal.Identity?.Name;
        if (string.IsNullOrWhiteSpace(accountName))
        {
            accountName = configuration["Authentication:DevelopmentAccount"] ?? "SONAERO\\engineering.admin";
        }

        var access = await ResolveAccessAsync(accountName, cancellationToken);
        if (access is null || !access.IsEnabled)
        {
            throw new UnauthorizedAccessException("No active Engineering module assignment was found.");
        }

        return new MeDto(
            accountName,
            ToDisplayName(accountName),
            access.Role,
            EngineeringAuthorization.PermissionsForRole(access.Role));
    }

    public async Task<EngineeringModuleAccess?> ResolveAccessAsync(
        string? accountName,
        CancellationToken cancellationToken = default)
    {
        var resolvedAccount = string.IsNullOrWhiteSpace(accountName)
            ? configuration["Authentication:DevelopmentAccount"] ?? "SONAERO\\engineering.admin"
            : accountName;

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
        claims.Add(new Claim(ClaimTypes.Role, access.Role));
        claims.AddRange(EngineeringAuthorization.PermissionsForRole(access.Role)
            .Select(permission => new Claim(EngineeringAuthorization.PermissionClaimType, permission)));

        var identity = new ClaimsIdentity(claims, principal.Identity?.AuthenticationType, ClaimTypes.Name, ClaimTypes.Role);
        return new ClaimsPrincipal(identity);
    }

    private static string ToDisplayName(string accountName)
    {
        var name = accountName;
        var separator = name.LastIndexOf('\\');
        if (separator >= 0 && separator < name.Length - 1)
        {
            name = name[(separator + 1)..];
        }

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
