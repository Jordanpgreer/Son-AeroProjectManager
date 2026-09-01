using System.Security.Cryptography;
using System.Text;

namespace SonAero.Platform.Security;

/// <summary>
/// A short-lived, administrator-issued session used to preview an application's
/// authorization surface as another user or a single permission group.
/// </summary>
public sealed class AccessPreviewSessionRecord
{
    public Guid Id { get; set; }
    public string TokenHash { get; set; } = string.Empty;
    public string AdministratorAccountName { get; set; } = string.Empty;
    public string TargetKey { get; set; } = string.Empty;
    public string ApplicationId { get; set; } = string.Empty;
    public DateTimeOffset IssuedAt { get; set; }
    public DateTimeOffset LaunchExpiresAt { get; set; }
    public DateTimeOffset SessionExpiresAt { get; set; }
    public DateTimeOffset? RedeemedAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
}

public static class AccessPreviewApplications
{
    public const string ProjectTracker = "project-tracker";
    public const string Engineering = "engineering-hub";
    public const string Estimating = "estimating-dashboard";
    public const string QualityAssurance = "quality-assurance";
}

public static class AccessPreviewTargetKinds
{
    public const string User = "user";
    public const string ProjectTrackerGroup = "project-tracker-group";
    public const string EngineeringGroup = "engineering-group";
}

public readonly record struct AccessPreviewTarget(string Kind, int Id)
{
    public static bool TryParse(string? value, out AccessPreviewTarget target)
    {
        target = default;
        if (string.IsNullOrWhiteSpace(value)) return false;

        var separator = value.LastIndexOf(':');
        if (separator <= 0 || separator == value.Length - 1) return false;
        if (!int.TryParse(value[(separator + 1)..], out var id) || id <= 0) return false;

        var kind = value[..separator].Trim().ToLowerInvariant();
        if (kind is not (AccessPreviewTargetKinds.User
            or AccessPreviewTargetKinds.ProjectTrackerGroup
            or AccessPreviewTargetKinds.EngineeringGroup))
        {
            return false;
        }

        target = new AccessPreviewTarget(kind, id);
        return true;
    }
}

public static class AccessPreviewTokens
{
    public static string Create()
    {
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        return token.TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    public static string Hash(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}

public static class AccessPreviewClaimTypes
{
    public const string Active = "sonaero.access-preview.active";
    public const string ActorAccountName = "sonaero.access-preview.actor";
    public const string TargetKey = "sonaero.access-preview.target-key";
    public const string TargetTitle = "sonaero.access-preview.target-title";
    public const string TargetAccountName = "sonaero.access-preview.target-account";
    public const string ApplicationId = "sonaero.access-preview.application";
}

public static class AccessPreviewRequests
{
    public static bool IsReadOnlyMethod(string method) =>
        string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase)
        || string.Equals(method, "HEAD", StringComparison.OrdinalIgnoreCase)
        || string.Equals(method, "OPTIONS", StringComparison.OrdinalIgnoreCase);
}
