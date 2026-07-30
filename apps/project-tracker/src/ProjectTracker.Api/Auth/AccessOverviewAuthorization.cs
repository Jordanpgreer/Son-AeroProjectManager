using System.Security.Claims;
using SonAero.Platform.Security;

namespace ProjectTracker.Api.Auth;

public static class AccessOverviewAuthorization
{
    public const string PolicyName = "ManageAccessOverview";

    public static bool IsAllowed(ClaimsPrincipal user) =>
        user.HasClaim(
            ApplicationClaimTypes.Permission,
            ApplicationPermissions.AccessManageUsers)
        || user.HasClaim(
            ApplicationClaimTypes.Permission,
            ApplicationPermissions.AccessManageGroups);
}
