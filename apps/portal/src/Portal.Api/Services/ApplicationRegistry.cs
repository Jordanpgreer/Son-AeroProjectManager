using Portal.Api.Models;
using Portal.Api.Dtos;

namespace Portal.Api.Services;

/// <summary>
/// Loads the application catalog from configuration ("Portal:Applications") and exposes it
/// filtered by role. Adding an application is a configuration change only.
/// </summary>
public sealed class ApplicationRegistry
{
    public const string AdminConsoleApplicationId = "admin-console";
    private readonly IReadOnlyList<ApplicationEntry> _applications;

    public ApplicationRegistry(IConfiguration configuration)
    {
        _applications = configuration.GetSection("Portal:Applications").Get<List<ApplicationEntry>>()
            ?? new List<ApplicationEntry>();
    }

    public IReadOnlyList<ApplicationEntry> All => _applications;

    public IReadOnlyList<ApplicationEntry> GetVisibleFor(MeDto currentUser)
    {
        if (currentUser.AccountStatus != PortalAccountStatus.Configured || currentUser.Role is null)
        {
            return [];
        }

        var accessibleModules = currentUser.Modules
            .Select(module => module.ModuleKey)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return GetVisibleFor(currentUser.Role, accessibleModules);
    }

    public IReadOnlyList<ApplicationEntry> GetVisibleFor(
        string role,
        IReadOnlySet<string>? accessibleModules = null)
        => _applications
            .Where(application =>
                IsVisibleTo(application, role)
                && IsModuleVisibleTo(application, accessibleModules))
            .OrderBy(application => application.Order)
            .ThenBy(application => application.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

    public static bool IsVisibleTo(ApplicationEntry application, string role)
    {
        if (string.Equals(application.Id, AdminConsoleApplicationId, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(role, SonAero.Platform.Security.ApplicationRoles.Admin, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return application.AllowedRoles is null or { Count: 0 }
            || application.AllowedRoles.Any(allowed => string.Equals(allowed, role, StringComparison.OrdinalIgnoreCase));
    }

    public static bool IsModuleVisibleTo(
        ApplicationEntry application,
        IReadOnlySet<string>? accessibleModules)
    {
        var moduleKey = application.Id switch
        {
            "engineering-hub" => SonAero.Platform.Security.ApplicationModules.Engineering,
            "estimating-dashboard" => SonAero.Platform.Security.ApplicationModules.Estimating,
            "quality-assurance" => SonAero.Platform.Security.ApplicationModules.QualityAssurance,
            _ => null
        };
        return moduleKey is null
            || accessibleModules is null
            || accessibleModules.Contains(moduleKey);
    }
}
