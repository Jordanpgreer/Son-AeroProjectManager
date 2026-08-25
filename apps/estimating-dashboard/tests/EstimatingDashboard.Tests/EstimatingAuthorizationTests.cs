using EstimatingDashboard.Api.Auth;
using System.Security.Claims;

namespace EstimatingDashboard.Tests;

public sealed class EstimatingAuthorizationTests
{
    [Theory]
    [InlineData(EstimatingRoles.Viewer, 3)]
    [InlineData(EstimatingRoles.Editor, 6)]
    [InlineData(EstimatingRoles.Admin, 9)]
    public void PermissionsAreCumulativeByRole(string role, int expectedCount)
    {
        var permissions = EstimatingPermissions.ForRole(role);

        Assert.Equal(expectedCount, permissions.Count);
        Assert.Contains(EstimatingPermissions.View, permissions);
        Assert.Contains(EstimatingPermissions.Calculate, permissions);
        Assert.Contains(EstimatingPermissions.ViewHistory, permissions);
    }

    [Fact]
    public void ViewerCannotMutatePersistentEstimatingState()
    {
        var permissions = EstimatingPermissions.ForRole(EstimatingRoles.Viewer);

        Assert.DoesNotContain(EstimatingPermissions.ManageQuotes, permissions);
        Assert.DoesNotContain(EstimatingPermissions.ManageInputs, permissions);
        Assert.DoesNotContain(EstimatingPermissions.AdministerRates, permissions);
        Assert.DoesNotContain(EstimatingPermissions.ImportHistory, permissions);
        Assert.DoesNotContain(EstimatingPermissions.ManageHistory, permissions);
    }

    [Fact]
    public void AdminIncludesRateAndSettingsCapabilities()
    {
        var permissions = EstimatingPermissions.ForRole(EstimatingRoles.Admin);

        Assert.Contains(EstimatingPermissions.ManageQuotes, permissions);
        Assert.Contains(EstimatingPermissions.ManageInputs, permissions);
        Assert.Contains(EstimatingPermissions.AdministerRates, permissions);
        Assert.Contains(EstimatingPermissions.AdministerSettings, permissions);
        Assert.Contains(EstimatingPermissions.ImportHistory, permissions);
        Assert.Contains(EstimatingPermissions.ManageHistory, permissions);
    }

    [Fact]
    public void EditorCannotViewTeamStatisticsReportsOrAuditHistory()
    {
        var permissions = EstimatingPermissions.ForRole(EstimatingRoles.Editor);

        Assert.DoesNotContain(EstimatingPermissions.ManageHistory, permissions);
    }

    [Fact]
    public void AttachedPrincipalCarriesExactRoleAndPermissionClaims()
    {
        var source = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.Name, "SONAERO\\admin")],
            "Test"));
        var access = new EstimatingAccessProfile(
            1,
            "SONAERO\\admin",
            "Admin",
            EstimatingRoles.Admin,
            true);

        var principal = EstimatingPolicies.Attach(source, access);

        Assert.True(principal.IsInRole(EstimatingRoles.Admin));
        Assert.Equal(
            EstimatingPermissions.ForRole(EstimatingRoles.Admin),
            principal.FindAll(EstimatingPolicies.PermissionClaim)
                .Select(claim => claim.Value)
                .ToList());
    }
}
