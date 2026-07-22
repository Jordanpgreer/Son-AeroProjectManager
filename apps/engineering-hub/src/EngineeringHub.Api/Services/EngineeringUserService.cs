using System.Security.Claims;
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

        var role = await ResolveRoleAsync(accountName, cancellationToken);
        return new MeDto(accountName, ToDisplayName(accountName), role);
    }

    public async Task<string> ResolveRoleAsync(string? accountName, CancellationToken cancellationToken = default)
    {
        var resolvedAccount = string.IsNullOrWhiteSpace(accountName)
            ? configuration["Authentication:DevelopmentAccount"] ?? "SONAERO\\engineering.admin"
            : accountName;

        var mode = configuration["Authentication:Mode"]
            ?? (string.IsNullOrEmpty(configuration["Authentication:DevelopmentAccount"]) ? "Windows" : "Development");

        if (string.Equals(mode, "Development", StringComparison.OrdinalIgnoreCase))
        {
            return ApplicationRoles.Normalize(configuration["Engineering:DevelopmentRole"]) ?? ApplicationRoles.Admin;
        }

        var storedRole = ApplicationRoles.Normalize(await roleStore.FindRoleAsync(resolvedAccount, cancellationToken));
        if (storedRole is not null)
        {
            return storedRole;
        }

        var admins = configuration.GetSection("Engineering:Admins").Get<string[]>() ?? [];
        if (admins.Any(account => string.Equals(account, resolvedAccount, StringComparison.OrdinalIgnoreCase)))
        {
            return ApplicationRoles.Admin;
        }

        return ApplicationRoles.Viewer;
    }

    public ClaimsPrincipal AttachRole(ClaimsPrincipal principal, string role)
    {
        var claims = principal.Claims.ToList();
        claims.RemoveAll(claim => claim.Type == ClaimTypes.Role);
        claims.Add(new Claim(ClaimTypes.Role, role));

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
