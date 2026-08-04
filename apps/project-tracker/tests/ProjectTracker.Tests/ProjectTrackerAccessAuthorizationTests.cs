using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using ProjectTracker.Api.Auth;
using SonAero.Platform.Security;

namespace ProjectTracker.Tests;

public sealed class ProjectTrackerAccessAuthorizationTests
{
    [Fact]
    public async Task CanView_RequiresRegistrationAndModuleViewPermission()
    {
        using var services = BuildServices();
        var authorization = services.GetRequiredService<IAuthorizationService>();

        Assert.False(await IsAuthorizedAsync(
            authorization,
            new Claim(ApplicationClaimTypes.RegisteredUser, "true")));
        Assert.False(await IsAuthorizedAsync(
            authorization,
            new Claim(ApplicationClaimTypes.Permission, ApplicationPermissions.ModuleView)));
        Assert.True(await IsAuthorizedAsync(
            authorization,
            new Claim(ApplicationClaimTypes.RegisteredUser, "true"),
            new Claim(ApplicationClaimTypes.Permission, ApplicationPermissions.ModuleView)));
    }

    private static ServiceProvider BuildServices()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAuthorization(options =>
            options.AddPolicy(
                ProjectTrackerAccessAuthorization.PolicyName,
                ProjectTrackerAccessAuthorization.ConfigurePolicy));
        return services.BuildServiceProvider();
    }

    private static async Task<bool> IsAuthorizedAsync(
        IAuthorizationService authorization,
        params Claim[] claims)
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"));
        var result = await authorization.AuthorizeAsync(
            principal,
            resource: null,
            ProjectTrackerAccessAuthorization.PolicyName);
        return result.Succeeded;
    }
}
