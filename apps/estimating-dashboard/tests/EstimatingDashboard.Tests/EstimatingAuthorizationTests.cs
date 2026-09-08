using EstimatingDashboard.Api.Auth;
using SonAero.Platform.Security;
using System.Security.Claims;

namespace EstimatingDashboard.Tests;

public sealed class EstimatingAuthorizationTests
{
    [Theory]
    [InlineData(EstimatingRoles.Viewer, 3)]
    [InlineData(EstimatingRoles.Editor, 5)]
    [InlineData(EstimatingRoles.Admin, 10)]
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
        Assert.Contains(EstimatingPermissions.DeleteQuotes, permissions);
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
        Assert.DoesNotContain(EstimatingPermissions.ImportHistory, permissions);
        Assert.DoesNotContain(EstimatingPermissions.DeleteQuotes, permissions);
    }

    [Fact]
    public void DeleteQuotesIsASeparateAdminDefaultAccessToggle()
    {
        var editor = ApplicationModuleCatalog
            .PermissionsFor(ApplicationModules.Estimating, ApplicationRoles.Editor)
            .Select(permission => permission.Key)
            .ToList();
        var administrator = ApplicationModuleCatalog
            .PermissionsFor(ApplicationModules.Estimating, ApplicationRoles.Admin)
            .Select(permission => permission.Key)
            .ToList();
        var permission = ApplicationModuleCatalog
            .PermissionsForModule(ApplicationModules.Estimating)
            .Single(candidate => candidate.Key == EstimatingPermissions.DeleteQuotes);

        Assert.DoesNotContain(EstimatingPermissions.DeleteQuotes, editor);
        Assert.Contains(EstimatingPermissions.DeleteQuotes, administrator);
        Assert.Equal("Delete quotes", permission.Label);
        Assert.Contains("published revisions", permission.Description);
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
