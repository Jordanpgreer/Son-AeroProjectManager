using Microsoft.AspNetCore.Authorization;
using SonAero.Platform.Security;

namespace ProjectTracker.Api.Auth;

public static class WalkthroughAuthorization
{
    public const string PolicyName = "ManageWalkthrough";

    public static void ConfigurePolicy(AuthorizationPolicyBuilder policy)
    {
        policy
            .RequireClaim(ApplicationClaimTypes.Group, ApplicationGroups.Administrators)
            .RequireClaim(ApplicationClaimTypes.Permission, ApplicationPermissions.AccessManageGroups);
    }
}
