using Portal.Api.Dtos;
using SonAero.Platform.Security;

namespace Portal.Api.Services;

/// <summary>
/// Resolves the current user's identity and portal role. In production the identity comes from
/// Windows Authentication; locally it comes from the development authentication handler. Role
/// resolution is intentionally lightweight — there is no central authorization database yet.
/// </summary>
public sealed class PortalUserService(
    IHttpContextAccessor httpContextAccessor,
    IConfiguration configuration,
    IPortalRoleStore roleStore)
{
    public async Task<MeDto> CurrentAsync(CancellationToken cancellationToken = default)
    {
        var principal = httpContextAccessor.HttpContext?.User;
        var accountName = principal?.Identity?.Name;
        if (string.IsNullOrWhiteSpace(accountName))
        {
            accountName = configuration["Authentication:DevelopmentAccount"] ?? "SONAERO\\dev.user";
        }

        return new MeDto(accountName, ToDisplayName(accountName), await ResolveRoleAsync(accountName, cancellationToken));
    }

    private async Task<string> ResolveRoleAsync(string accountName, CancellationToken cancellationToken)
    {
        var mode = configuration["Authentication:Mode"]
            ?? (string.IsNullOrEmpty(configuration["Authentication:DevelopmentAccount"]) ? "Windows" : "Development");

        if (string.Equals(mode, "Development", StringComparison.OrdinalIgnoreCase))
        {
            return ApplicationRoles.Normalize(configuration["Portal:DevelopmentRole"]) ?? ApplicationRoles.Admin;
        }

        var storedRole = ApplicationRoles.Normalize(await roleStore.FindRoleAsync(accountName, cancellationToken));
        if (storedRole is not null)
        {
            return storedRole;
        }

        var admins = configuration.GetSection("Portal:Admins").Get<string[]>() ?? Array.Empty<string>();
        var editors = configuration.GetSection("Portal:Editors").Get<string[]>() ?? Array.Empty<string>();

        if (admins.Any(account => string.Equals(account, accountName, StringComparison.OrdinalIgnoreCase)))
        {
            return ApplicationRoles.Admin;
        }

        if (editors.Any(account => string.Equals(account, accountName, StringComparison.OrdinalIgnoreCase)))
        {
            return ApplicationRoles.Editor;
        }

        return ApplicationRoles.Viewer;
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
