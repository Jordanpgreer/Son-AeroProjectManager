using System.Security.Claims;
using SonAero.Platform.Security;

namespace ProjectTracker.Api.Services;

public sealed class CurrentUserService(IHttpContextAccessor httpContextAccessor)
{
    private ClaimsPrincipal? Principal => httpContextAccessor.HttpContext?.User;

    public string AccountName => WindowsAccountNames.Normalize(
        httpContextAccessor.HttpContext?.User.Identity?.Name) ?? "Unknown";

    public string DisplayName
    {
        get
        {
            var account = AccountName;
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

