using Microsoft.AspNetCore.Authorization;
using SonAero.Platform.Security;

namespace ProjectTracker.Api.Auth;

public static class ProjectTrackerAccessAuthorization
{
    public const string PolicyName = "CanView";

    public static void ConfigurePolicy(AuthorizationPolicyBuilder policy)
    {
        policy.RequireClaim(ApplicationClaimTypes.RegisteredUser, "true");
        policy.RequireClaim(ApplicationClaimTypes.Permission, ApplicationPermissions.ModuleView);
    }
}
