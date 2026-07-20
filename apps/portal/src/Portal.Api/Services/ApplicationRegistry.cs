using Portal.Api.Models;

namespace Portal.Api.Services;

/// <summary>
/// Loads the application catalog from configuration ("Portal:Applications") and exposes it
/// filtered by role. Adding an application is a configuration change only.
/// </summary>
public sealed class ApplicationRegistry
{
    private readonly IReadOnlyList<ApplicationEntry> _applications;

    public ApplicationRegistry(IConfiguration configuration)
    {
        _applications = configuration.GetSection("Portal:Applications").Get<List<ApplicationEntry>>()
            ?? new List<ApplicationEntry>();
    }

    public IReadOnlyList<ApplicationEntry> All => _applications;

    public IReadOnlyList<ApplicationEntry> GetVisibleFor(string role)
        => _applications
            .Where(application => IsVisibleTo(application, role))
            .OrderBy(application => application.Order)
            .ThenBy(application => application.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

    public static bool IsVisibleTo(ApplicationEntry application, string role)
        => application.AllowedRoles is null or { Count: 0 }
           || application.AllowedRoles.Any(allowed => string.Equals(allowed, role, StringComparison.OrdinalIgnoreCase));
}
