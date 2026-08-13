using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ProjectTracker.Api.Configuration;

namespace ProjectTracker.Tests;

public sealed class HubCorsPolicyTests
{
    [Fact]
    public async Task AddHubCors_ConfiguresExactOriginsAndCredentialedRequests()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Cors:HubOrigins:0"] = "http://localhost:5140/",
                ["Cors:HubOrigins:1"] = "https://hub.internal.example"
            })
            .Build();
        var services = new ServiceCollection();

        services.AddHubCors(configuration);

        await using var provider = services.BuildServiceProvider();
        var policyProvider = provider.GetRequiredService<ICorsPolicyProvider>();
        var policy = await policyProvider.GetPolicyAsync(new DefaultHttpContext(), HubCorsPolicy.Name);

        Assert.NotNull(policy);
        Assert.Equal(
            ["http://localhost:5140", "https://hub.internal.example"],
            policy.Origins);
        Assert.True(policy.SupportsCredentials);
        Assert.True(policy.AllowAnyHeader);
        Assert.True(policy.AllowAnyMethod);
        Assert.False(policy.AllowAnyOrigin);
    }

    [Fact]
    public async Task AddHubCors_AllowsPermanentAndLegacyHubOriginsDuringRollbackWindow()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Cors:HubOrigins:0"] = "https://hub.son4l.local",
                ["Cors:HubOrigins:1"] = "http://SON-IIS2:5140"
            })
            .Build();
        var services = new ServiceCollection();

        services.AddHubCors(configuration);

        await using var provider = services.BuildServiceProvider();
        var policyProvider = provider.GetRequiredService<ICorsPolicyProvider>();
        var policy = await policyProvider.GetPolicyAsync(new DefaultHttpContext(), HubCorsPolicy.Name);

        Assert.NotNull(policy);
        Assert.Equal(
            ["https://hub.son4l.local", "http://son-iis2:5140"],
            policy.Origins,
            StringComparer.OrdinalIgnoreCase);
        Assert.True(policy.SupportsCredentials);
    }

    [Fact]
    public async Task CorsService_AllowsBrowserNormalizedLegacyOriginAndRejectsMixedCaseHost()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Cors:HubOrigins:0"] = "https://SON-IIS2:6140"
            })
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddHubCors(configuration);

        await using var provider = services.BuildServiceProvider();
        var policyProvider = provider.GetRequiredService<ICorsPolicyProvider>();
        var corsService = provider.GetRequiredService<ICorsService>();
        var normalizedContext = new DefaultHttpContext();
        normalizedContext.Request.Headers.Origin = "https://son-iis2:6140";
        var policy = await policyProvider.GetPolicyAsync(normalizedContext, HubCorsPolicy.Name);

        Assert.NotNull(policy);
        Assert.True(corsService.EvaluatePolicy(normalizedContext, policy).IsOriginAllowed);

        var mixedCaseContext = new DefaultHttpContext();
        mixedCaseContext.Request.Headers.Origin = "https://SON-IIS2:6140";
        Assert.False(corsService.EvaluatePolicy(mixedCaseContext, policy).IsOriginAllowed);
    }

    [Fact]
    public void AddHubCors_RejectsWildcardOriginWithCredentials()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Cors:HubOrigins:0"] = "*"
            })
            .Build();
        var services = new ServiceCollection();

        var exception = Assert.Throws<InvalidOperationException>(
            () => services.AddHubCors(configuration));

        Assert.Contains("cannot contain '*'", exception.Message);
    }

    [Theory]
    [InlineData("http://localhost:5140/admin")]
    [InlineData("ftp://localhost:5140")]
    [InlineData("not-an-origin")]
    public void AddHubCors_RejectsValuesThatAreNotHttpOrigins(string origin)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Cors:HubOrigins:0"] = origin
            })
            .Build();
        var services = new ServiceCollection();

        Assert.Throws<InvalidOperationException>(() => services.AddHubCors(configuration));
    }
}
