using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Portal.Api.Data;
using Portal.Api.Endpoints;
using SonAero.Platform.Security;

namespace Portal.Tests;

public sealed class EstimatingAdminEndpointTests
{
    [Fact]
    public void Estimating_estimator_admin_routes_require_authenticated_users()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddAuthorization();
        builder.Services.AddDbContext<PortalRoleDbContext>(options =>
            options.UseSqlite("Data Source=:memory:"));
        var app = builder.Build();
        app.MapGroup("/api").MapEstimatingAdminEndpoints();

        var routes = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Where(endpoint => endpoint.RoutePattern.RawText?.StartsWith("/api/admin/estimating/estimators") == true)
            .ToList();

        Assert.Equal(2, routes.Count);
        Assert.All(routes, endpoint => Assert.NotEmpty(endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>()));
    }

    [Fact]
    public void Permission_catalog_describes_the_existing_logs_toggle_explicitly()
    {
        var permission = ApplicationModuleCatalog
            .PermissionsForModule(ApplicationModules.Estimating)
            .Single(candidate => candidate.Key == "estimating.history.view");

        Assert.Equal("View Estimating Logs", permission.Label);
        Assert.Contains("Open Estimating Logs", permission.Description);
    }

    [Fact]
    public void Portal_context_maps_persistent_estimator_settings()
    {
        var options = new DbContextOptionsBuilder<PortalRoleDbContext>()
            .UseSqlite("Data Source=:memory:")
            .Options;
        using var db = new PortalRoleDbContext(options);
        var entity = db.Model.FindEntityType(typeof(PortalEstimatorSettingRecord))!;

        Assert.Equal("EstimatingEstimatorSettings", entity.GetTableName());
        Assert.Equal(nameof(PortalEstimatorSettingRecord.EstimatorKey), entity.FindPrimaryKey()!.Properties.Single().Name);
    }
}
