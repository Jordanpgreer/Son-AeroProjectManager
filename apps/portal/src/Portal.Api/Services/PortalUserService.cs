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

        var account = await roleStore.FindAccountAsync(accountName, cancellationToken);
        var isDevelopment = IsDevelopmentMode();
        var bootstrapRole = ResolveBootstrapRole(accountName);
        var status = ResolveStatus(account, isDevelopment, bootstrapRole);
        var role = ResolveRole(account, status, isDevelopment, bootstrapRole);
        var moduleRoles = status == PortalAccountStatus.Configured
            ? account.ModuleRoles
            : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (moduleRoles.Count == 0 && isDevelopment)
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

        return new MeDto(
            accountName,
            string.IsNullOrWhiteSpace(account.DisplayName) ? ToDisplayName(accountName) : account.DisplayName,
            status,
            role,
            modules);
    }

    private static PortalAccountStatus ResolveStatus(
        PortalAccountLookup account,
        bool isDevelopment,
        string? bootstrapRole)
    {
        if (isDevelopment)
        {
            return PortalAccountStatus.Configured;
        }

        if (account.Status == PortalAccountLookupStatus.Found)
        {
            if (!account.IsActive) return PortalAccountStatus.Inactive;
            return account.HasProjectTrackerAccess || account.ModuleRoles.Count > 0
                ? PortalAccountStatus.Configured
                : PortalAccountStatus.PendingSetup;
        }

        if (bootstrapRole is not null) return PortalAccountStatus.Configured;
        return account.Status == PortalAccountLookupStatus.Unavailable
            ? PortalAccountStatus.Unavailable
            : PortalAccountStatus.PendingSetup;
    }

    private string? ResolveRole(
        PortalAccountLookup account,
        PortalAccountStatus status,
        bool isDevelopment,
        string? bootstrapRole)
    {
        if (status != PortalAccountStatus.Configured)
        {
            return null;
        }

        if (isDevelopment)
        {
            return ApplicationRoles.Normalize(configuration["Portal:DevelopmentRole"]) ?? ApplicationRoles.Admin;
        }

        if (account.Status == PortalAccountLookupStatus.Found)
        {
            return ApplicationRoles.Normalize(account.Role) ?? ApplicationRoles.Viewer;
        }

        return bootstrapRole;
    }

    private string? ResolveBootstrapRole(string accountName)
    {
        var admins = configuration.GetSection("Portal:Admins").Get<string[]>() ?? [];
        if (admins.Any(account => WindowsAccountNames.Equals(account, accountName)))
            return ApplicationRoles.Admin;

        var editors = configuration.GetSection("Portal:Editors").Get<string[]>() ?? [];
        return editors.Any(account => WindowsAccountNames.Equals(account, accountName))
            ? ApplicationRoles.Editor
            : null;
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
