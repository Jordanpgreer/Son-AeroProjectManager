using System.Security.Claims;
using SonAero.Platform.Security;

namespace ProjectTracker.Api.Services;

public sealed class CurrentUserService(IHttpContextAccessor httpContextAccessor)
{
    private ClaimsPrincipal? Principal => httpContextAccessor.HttpContext?.User;

    public string ActorAccountName => WindowsAccountNames.Normalize(
        httpContextAccessor.HttpContext?.User.Identity?.Name) ?? "Unknown";

    public string AccountName => ActorAccountName;

    public bool IsAccessPreview => Principal?.HasClaim(AccessPreviewClaimTypes.Active, "true") == true;

    public string? PreviewTargetKey => Principal?.FindFirstValue(AccessPreviewClaimTypes.TargetKey);

    public string? PreviewTargetTitle => Principal?.FindFirstValue(AccessPreviewClaimTypes.TargetTitle);

    public string? PreviewTargetAccountName => Principal?.FindFirstValue(AccessPreviewClaimTypes.TargetAccountName);

    public string? EffectiveAccountName => IsAccessPreview ? PreviewTargetAccountName : ActorAccountName;

    public string DisplayName
    {
        get
        {
            if (IsAccessPreview && !string.IsNullOrWhiteSpace(PreviewTargetTitle)) return PreviewTargetTitle;
            var configuredDisplayName = Principal?.FindFirstValue(ApplicationClaimTypes.DisplayName);
            if (!string.IsNullOrWhiteSpace(configuredDisplayName)) return configuredDisplayName;
            var account = ActorAccountName;
            return WindowsAccountNames.DisplayName(account);
        }
    }

    public bool IsRegistered => Principal?.HasClaim(ApplicationClaimTypes.RegisteredUser, "true") == true;

    public bool IsActive => IsRegistered;

    public IReadOnlyList<string> Groups => Principal?
        .FindAll(ApplicationClaimTypes.Group)
        .Select(claim => claim.Value)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .OrderBy(value => value)
        .ToList() ?? [];

    public IReadOnlyList<string> Permissions => Principal?
        .FindAll(ApplicationClaimTypes.Permission)
        .Select(claim => claim.Value)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .OrderBy(value => value)
        .ToList() ?? [];

    public bool HasPermission(string permission) => Principal?.HasClaim(ApplicationClaimTypes.Permission, permission) == true;
}

