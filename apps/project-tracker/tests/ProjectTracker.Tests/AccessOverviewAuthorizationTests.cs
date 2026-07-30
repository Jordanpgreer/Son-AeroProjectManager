using System.Security.Claims;
using ProjectTracker.Api.Auth;
using SonAero.Platform.Security;

namespace ProjectTracker.Tests;

public sealed class AccessOverviewAuthorizationTests
{
    [Fact]
    public void IsAllowed_WithManageUsersOnly_ReturnsTrue()
    {
        var user = PrincipalWithPermissions(ApplicationPermissions.AccessManageUsers);

        Assert.True(AccessOverviewAuthorization.IsAllowed(user));
    }

    [Fact]
    public void IsAllowed_WithManageGroupsOnly_ReturnsTrue()
    {
        var user = PrincipalWithPermissions(ApplicationPermissions.AccessManageGroups);

        Assert.True(AccessOverviewAuthorization.IsAllowed(user));
    }

    [Fact]
    public void IsAllowed_WithBothPermissions_ReturnsTrue()
    {
        var user = PrincipalWithPermissions(
            ApplicationPermissions.AccessManageUsers,
            ApplicationPermissions.AccessManageGroups);

        Assert.True(AccessOverviewAuthorization.IsAllowed(user));
    }

    [Fact]
    public void IsAllowed_WithNeitherPermission_ReturnsFalse()
    {
        var user = PrincipalWithPermissions(ApplicationPermissions.ModuleView);

        Assert.False(AccessOverviewAuthorization.IsAllowed(user));
    }

    private static ClaimsPrincipal PrincipalWithPermissions(params string[] permissions)
    {
        var claims = permissions.Select(permission =>
            new Claim(ApplicationClaimTypes.Permission, permission));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"));
    }
}
