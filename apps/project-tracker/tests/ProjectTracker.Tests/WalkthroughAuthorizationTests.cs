using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using ProjectTracker.Api.Auth;
using SonAero.Platform.Security;

namespace ProjectTracker.Tests;

public sealed class WalkthroughAuthorizationTests
{
    [Theory]
    [InlineData(true, true, true)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, false)]
    public async Task ManageWalkthrough_RequiresAdministratorAndGroupManagement(
        bool isAdministrator,
        bool canManageGroups,
        bool expected)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAuthorization(options =>
            options.AddPolicy(
                WalkthroughAuthorization.PolicyName,
                WalkthroughAuthorization.ConfigurePolicy));
        using var provider = services.BuildServiceProvider();
        var authorization = provider.GetRequiredService<IAuthorizationService>();
        var claims = new List<Claim>();
        if (isAdministrator)
        {
            claims.Add(new Claim(ApplicationClaimTypes.Group, ApplicationGroups.Administrators));
        }
        if (canManageGroups)
        {
            claims.Add(new Claim(
                ApplicationClaimTypes.Permission,
                ApplicationPermissions.AccessManageGroups));
        }

        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"));
        var result = await authorization.AuthorizeAsync(
            principal,
            resource: null,
            WalkthroughAuthorization.PolicyName);

        Assert.Equal(expected, result.Succeeded);
    }
}
