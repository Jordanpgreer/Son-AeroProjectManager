using EstimatingDashboard.Api.Auth;
using EstimatingDashboard.Api.Endpoints;
using EstimatingDashboard.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace EstimatingDashboard.Tests;

public sealed class FulcrumEstimateEndpointAuthorizationTests
{
    [Fact]
    public void Routes_require_view_and_mutations_require_scoped_permissions()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddAuthorization();
        builder.Services.AddScoped<FulcrumEstimateImportService>();
        builder.Services.AddScoped<FulcrumEstimateExportService>();
        builder.Services.AddScoped<EstimatingOperationMappingService>();
        var app = builder.Build();
        app.MapGroup("/api").MapFulcrumEstimateEndpoints();

        var routes = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Where(endpoint => endpoint.RoutePattern.RawText?.StartsWith("/api/fulcrum-estimates") == true)
            .ToList();
        Assert.Equal(6, routes.Count);
        Assert.All(routes, endpoint => Assert.Contains(
            endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>(),
            authorization => authorization.Policy == EstimatingPolicies.Viewer));

        foreach (var endpoint in routes.Where(endpoint =>
            endpoint.RoutePattern.RawText is "/api/fulcrum-estimates/preview"
                or "/api/fulcrum-estimates/{reviewId:guid}/export"))
            Assert.Contains(
                endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>(),
                authorization => authorization.Policy == EstimatingPolicies.ManageInputs);

        var mutations = routes.Where(endpoint =>
            endpoint.RoutePattern.RawText?.Contains("/rules") == true
            && endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()!.HttpMethods.Any(method => method != "GET"));
        Assert.Equal(3, mutations.Count());
        Assert.All(mutations, endpoint => Assert.Contains(
            endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>(),
            authorization => authorization.Policy == EstimatingPolicies.AdministerRates));
    }
}
