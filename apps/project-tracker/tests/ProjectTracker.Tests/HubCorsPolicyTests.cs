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
