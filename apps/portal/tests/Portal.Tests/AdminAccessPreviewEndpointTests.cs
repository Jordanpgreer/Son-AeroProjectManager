using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Portal.Api.Data;
using Portal.Api.Endpoints;
using Portal.Api.Services;
using SonAero.Platform.Security;

namespace Portal.Tests;

public sealed class AdminAccessPreviewEndpointTests
{
    [Fact]
    public void Access_preview_routes_require_authenticated_users()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddAuthorization();
        builder.Services.AddScoped<PortalUserService>();
        builder.Services.AddSingleton<ApplicationRegistry>();
        builder.Services.AddDbContext<PortalRoleDbContext>(options =>
            options.UseSqlite("Data Source=:memory:"));
        var app = builder.Build();
        app.MapGroup("/api").MapAdminAccessPreviewEndpoints();

        var routes = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Where(endpoint => endpoint.RoutePattern.RawText?.StartsWith("/api/admin/access-previews") == true)
            .ToList();

        Assert.Equal(3, routes.Count);
        Assert.All(routes, endpoint => Assert.NotEmpty(endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>()));
    }

    [Fact]
    public void Walkthrough_preview_route_is_an_authenticated_extension_of_access_preview()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddAuthorization();
        builder.Services.AddScoped<PortalUserService>();
        builder.Services.AddSingleton<ApplicationRegistry>();
        builder.Services.AddDbContext<PortalRoleDbContext>(options =>
            options.UseSqlite("Data Source=:memory:"));
        var app = builder.Build();
        app.MapGroup("/api").MapAdminAccessPreviewEndpoints();

        var route = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Single(endpoint => endpoint.RoutePattern.RawText == "/api/admin/access-previews/{targetKey}/walkthrough");

        Assert.Contains(HttpMethods.Post, route.Metadata.GetMetadata<IHttpMethodMetadata>()!.HttpMethods);
        Assert.NotEmpty(route.Metadata.GetOrderedMetadata<IAuthorizeData>());
    }

    [Theory]
    [InlineData(false, "https://projects.example.test/access-preview/start")]
    [InlineData(true, "https://projects.example.test/access-preview/start?experience=walkthrough")]
    public void Walkthrough_launch_reuses_the_access_preview_start_endpoint(
        bool walkthrough,
        string expected)
    {
        var configuredApplication = new Uri("https://projects.example.test/ignored/path?old=value#fragment");

        var result = AdminAccessPreviewEndpoints.BuildStartUri(configuredApplication, walkthrough);

        Assert.Equal(expected, result.ToString());
    }

    [Fact]
    public void Preview_tokens_are_random_and_only_their_hash_is_stable()
    {
        var first = AccessPreviewTokens.Create();
        var second = AccessPreviewTokens.Create();

        Assert.NotEqual(first, second);
        Assert.Equal(64, AccessPreviewTokens.Hash(first).Length);
        Assert.Equal(AccessPreviewTokens.Hash(first), AccessPreviewTokens.Hash(first));
        Assert.NotEqual(AccessPreviewTokens.Hash(first), AccessPreviewTokens.Hash(second));
    }

    [Theory]
    [InlineData("user:14", AccessPreviewTargetKinds.User, 14)]
    [InlineData("project-tracker-group:7", AccessPreviewTargetKinds.ProjectTrackerGroup, 7)]
    [InlineData("engineering-group:3", AccessPreviewTargetKinds.EngineeringGroup, 3)]
    public void Preview_target_keys_are_strictly_typed(string value, string expectedKind, int expectedId)
    {
        Assert.True(AccessPreviewTarget.TryParse(value, out var target));
        Assert.Equal(expectedKind, target.Kind);
        Assert.Equal(expectedId, target.Id);
    }

    [Theory]
    [InlineData("")]
    [InlineData("user:0")]
    [InlineData("user:-1")]
    [InlineData("unknown:1")]
    [InlineData("user:not-an-id")]
    public void Invalid_preview_target_keys_are_rejected(string value)
    {
        Assert.False(AccessPreviewTarget.TryParse(value, out _));
    }
}
