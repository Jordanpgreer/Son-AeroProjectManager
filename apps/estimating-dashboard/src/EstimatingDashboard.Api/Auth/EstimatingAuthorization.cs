using System.Security.Claims;

namespace EstimatingDashboard.Api.Auth;

public static class EstimatingModule
{
    public const string Key = "estimating";
}

public static class EstimatingRoles
{
    public const string Viewer = "Viewer";
    public const string Editor = "Editor";
    public const string Admin = "Admin";

    public static string? Normalize(string? role)
    {
        if (string.Equals(role, Viewer, StringComparison.OrdinalIgnoreCase)) return Viewer;
        if (string.Equals(role, Editor, StringComparison.OrdinalIgnoreCase)) return Editor;
        if (string.Equals(role, Admin, StringComparison.OrdinalIgnoreCase)) return Admin;
        return null;
    }
}

public static class EstimatingPermissions
{
    public const string View = "estimating.view";
    public const string Calculate = "estimating.calculate";
    public const string ManageQuotes = "estimating.quotes.manage";
    public const string ManageInputs = "estimating.inputs.manage";
    public const string AdministerRates = "estimating.rates.admin";
    public const string AdministerSettings = "estimating.settings.admin";
    public const string ViewHistory = "estimating.history.view";
    public const string ImportHistory = "estimating.history.import";

    private static readonly IReadOnlyList<string> ViewerPermissions =
    [
        View,
        Calculate,
        ViewHistory
    ];

    private static readonly IReadOnlyList<string> EditorPermissions =
    [
        .. ViewerPermissions,
        ManageQuotes,
        ManageInputs,
        ImportHistory
    ];

    private static readonly IReadOnlyList<string> AdminPermissions =
    [
        .. EditorPermissions,
        AdministerRates,
        AdministerSettings
    ];

    public static IReadOnlyList<string> ForRole(string role) => EstimatingRoles.Normalize(role) switch
    {
        EstimatingRoles.Admin => AdminPermissions,
        EstimatingRoles.Editor => EditorPermissions,
        EstimatingRoles.Viewer => ViewerPermissions,
        _ => []
    };
}

public static class EstimatingPolicies
{
    public const string Viewer = "EstimatingViewer";
    public const string Editor = "EstimatingEditor";
    public const string Admin = "EstimatingAdmin";
    public const string Calculate = "EstimatingCalculate";
    public const string ManageInputs = "EstimatingManageInputs";
    public const string AdministerRates = "EstimatingAdministerRates";
    public const string ViewHistory = "EstimatingViewHistory";
    public const string ImportHistory = "EstimatingImportHistory";
    public const string PermissionClaim = "sonaero.permission";
    public const string AccessItem = "EstimatingAccess";

    public static ClaimsPrincipal Attach(
        ClaimsPrincipal principal,
        EstimatingAccessProfile access)
    {
        var claims = principal.Claims
            .Where(claim => claim.Type is not ClaimTypes.Role && claim.Type != PermissionClaim)
            .ToList();
        claims.Add(new Claim(ClaimTypes.Role, access.Role));
        claims.AddRange(access.Permissions.Select(
            permission => new Claim(PermissionClaim, permission)));
        claims.RemoveAll(claim => claim.Type.StartsWith("sonaero.access-preview.", StringComparison.Ordinal));
        if (access.IsPreview)
        {
            claims.Add(new Claim(SonAero.Platform.Security.AccessPreviewClaimTypes.Active, bool.TrueString));
            claims.Add(new Claim(SonAero.Platform.Security.AccessPreviewClaimTypes.ApplicationId, SonAero.Platform.Security.AccessPreviewApplications.Estimating));
            if (!string.IsNullOrWhiteSpace(access.PreviewActorAccountName))
                claims.Add(new Claim(SonAero.Platform.Security.AccessPreviewClaimTypes.ActorAccountName, access.PreviewActorAccountName));
            if (!string.IsNullOrWhiteSpace(access.PreviewTargetKey))
                claims.Add(new Claim(SonAero.Platform.Security.AccessPreviewClaimTypes.TargetKey, access.PreviewTargetKey));
            claims.Add(new Claim(SonAero.Platform.Security.AccessPreviewClaimTypes.TargetTitle, access.DisplayName));
            claims.Add(new Claim(SonAero.Platform.Security.AccessPreviewClaimTypes.TargetAccountName, access.AccountName));
        }
        var identity = new ClaimsIdentity(
            claims,
            principal.Identity?.AuthenticationType,
            ClaimTypes.Name,
            ClaimTypes.Role);
        return new ClaimsPrincipal(identity);
    }
}

public sealed record EstimatingAccessProfile(
    int UserId,
    string AccountName,
    string DisplayName,
    string Role,
    bool IsEnabled,
    bool IsPreview = false,
    string? PreviewActorAccountName = null,
    string? PreviewTargetKey = null,
    IReadOnlyList<string>? GrantedPermissions = null)
{
    public IReadOnlyList<string> Permissions => GrantedPermissions ?? EstimatingPermissions.ForRole(Role);
}
