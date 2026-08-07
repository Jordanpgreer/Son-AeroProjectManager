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

        accountName = WindowsAccountNames.Normalize(accountName)
            ?? throw new UnauthorizedAccessException("A valid Windows account name is required.");

        var role = await ResolveRoleAsync(accountName, cancellationToken);
        var moduleRoles = await roleStore.FindModuleRolesAsync(accountName, cancellationToken);
        if (moduleRoles.Count == 0 && IsDevelopmentMode())
        {
            var developmentRole = ApplicationModuleRoles.Normalize(role) ?? ApplicationRoles.Admin;
            moduleRoles = ApplicationModuleCatalog.All
                .Where(module => module.Roles.Any(candidate => candidate.Role == developmentRole))
                .ToDictionary(
                    module => module.Key,
                    _ => developmentRole,
                    StringComparer.OrdinalIgnoreCase);
        }

        var modules = moduleRoles
            .Where(access => ApplicationModuleCatalog.Find(access.Key)?.Roles.Any(
                candidate => candidate.Role == access.Value) == true)
            .Select(access => new PortalModuleAccessDto(
                access.Key,
                access.Value,
                ApplicationModuleCatalog.PermissionsFor(access.Key, access.Value)
                    .Select(permission => permission.Key)
                    .ToList()))
            .OrderBy(access => access.ModuleKey, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new MeDto(accountName, ToDisplayName(accountName), role, modules);
    }

    private async Task<string> ResolveRoleAsync(string accountName, CancellationToken cancellationToken)
    {
        if (IsDevelopmentMode())
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

        if (admins.Any(account => WindowsAccountNames.Equals(account, accountName)))
        {
            return ApplicationRoles.Admin;
        }

        if (editors.Any(account => WindowsAccountNames.Equals(account, accountName)))
        {
            return ApplicationRoles.Editor;
        }

        return ApplicationRoles.Viewer;
    }

    private bool IsDevelopmentMode()
    {
        var mode = configuration["Authentication:Mode"]
            ?? (string.IsNullOrEmpty(configuration["Authentication:DevelopmentAccount"]) ? "Windows" : "Development");
        return string.Equals(mode, "Development", StringComparison.OrdinalIgnoreCase);
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
