using EstimatingDashboard.Api.Auth;
using EstimatingDashboard.Api.Endpoints;
using EstimatingDashboard.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace EstimatingDashboard.Tests;

public sealed class EstimatingHistoryEndpointAuthorizationTests
{
    [Fact]
    public void Every_estimating_logs_route_requires_the_view_logs_permission()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddAuthorization();
        builder.Services.AddScoped<EstimatingHistoryQueryService>();
        builder.Services.AddScoped<EstimatingHistoryImportService>();
        builder.Services.AddScoped<EstimatingHistoryReportService>();
        builder.Services.AddScoped<EstimatingHistoryGridExportService>();
        builder.Services.AddScoped<EstimatorSummaryReportService>();
        var app = builder.Build();
        app.MapGroup("/api").MapEstimatingHistoryEndpoints();

        var routes = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Where(endpoint => endpoint.RoutePattern.RawText?.StartsWith("/api/quote-history") == true)
            .ToList();

        Assert.NotEmpty(routes);
        Assert.All(routes, endpoint => Assert.Contains(
            endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>(),
            authorization => authorization.Policy == EstimatingPolicies.ViewHistory));
    }
}
