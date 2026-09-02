using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ProjectTracker.Api.Data;
using ProjectTracker.Api.Endpoints;
using ProjectTracker.Api.Services;
using SonAero.Platform.Integrations;

namespace ProjectTracker.Tests;

public sealed class ProjectQuantityEndpointTests
{
    [Fact]
    public void Current_and_legacy_quantity_routes_require_the_quantity_permission_policy()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddAuthorization();
        builder.Services.AddDbContext<ProjectTrackerDbContext>(options =>
            options.UseSqlite("Data Source=:memory:"));
        builder.Services.AddScoped<ProjectAuditService>();
        builder.Services.AddScoped<IProjectQuantityProvider, AcumaticaProjectQuantityProvider>();
        builder.Services.AddScoped<IEnterpriseProviderSource, StubProviderSource>();
        var app = builder.Build();
        app.MapGroup("/api").MapProjectQuantitySyncEndpoints();

        var routes = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Where(endpoint => endpoint.RoutePattern.RawText?.StartsWith(
                "/api/projects/{projectId:int}/quantities/sync") == true)
            .ToList();

        Assert.Equal(2, routes.Count);
        Assert.All(routes, endpoint => Assert.Contains(
            endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>(),
            authorization => authorization.Policy == ProjectQuantitySyncEndpoints.AuthorizationPolicy));
    }

    private sealed class StubProviderSource : IEnterpriseProviderSource
    {
        public Task<string> GetActiveProviderAsync(CancellationToken cancellationToken) =>
            Task.FromResult(EnterpriseProviderNames.Fulcrum);
    }
}
