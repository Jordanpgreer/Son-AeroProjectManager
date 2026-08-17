using ProjectTracker.Api.Auth;
using ProjectTracker.Api.Models;

namespace ProjectTracker.Api.Services;

public static class ProjectExternalLinks
{
    public const int MaxLength = 2048;

    public static bool TryNormalize(
        string? value,
        string label,
        out string? normalized,
        out string? error)
    {
        normalized = null;
        error = null;
        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        var candidate = value.Trim();
        if (candidate.Length > MaxLength)
        {
            error = $"{label} cannot exceed {MaxLength:N0} characters.";
            return false;
        }

        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri)
            || !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(uri.Host)
            || Uri.CheckHostName(uri.Host) == UriHostNameType.Unknown)
        {
            error = $"{label} must be an absolute HTTPS URL.";
            return false;
        }

        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            error = $"{label} cannot contain a username or password.";
            return false;
        }

        normalized = uri.AbsoluteUri;
        return true;
    }

    public static string? FindDeniedEditPermission(
        Project project,
        string? salesOrderUrl,
        string? jobUrl,
        Func<string, bool> hasPermission)
    {
        var changed = !string.Equals(project.SalesOrderUrl, salesOrderUrl, StringComparison.Ordinal)
            || !string.Equals(project.JobUrl, jobUrl, StringComparison.Ordinal);
        return changed && !hasPermission(ProjectTrackerPermissions.ProjectEditExternalLinks)
            ? ProjectTrackerPermissions.ProjectEditExternalLinks
            : null;
    }
}
