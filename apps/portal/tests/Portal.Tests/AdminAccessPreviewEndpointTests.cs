using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using System.Text;
using Portal.Api.Data;
using Portal.Api.Dtos;
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

    [Theory]
    [InlineData(ApplicationPermissions.ModuleView, AccessPreviewApplications.ProjectTracker)]
    [InlineData(EngineeringPermissions.ModuleView, AccessPreviewApplications.Engineering)]
    [InlineData("estimating.view", AccessPreviewApplications.Estimating)]
    [InlineData(QualityAssurancePermissions.ModuleView, AccessPreviewApplications.QualityAssurance)]
    public void Preview_uses_module_entry_permissions_for_granular_groups(
        string permission,
        string expectedApplicationId)
    {
        var applications = AdminAccessPreviewEndpoints.ApplicationsForAccess(
            BuildRegistry(),
            new HashSet<string>([permission], StringComparer.OrdinalIgnoreCase));

        Assert.Equal(expectedApplicationId, Assert.Single(applications).Id);
    }

    [Fact]
    public void User_preview_includes_legacy_module_assignments()
    {
        var applications = AdminAccessPreviewEndpoints.ApplicationsForAccess(
            BuildRegistry(),
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            new HashSet<string>([ApplicationModules.QualityAssurance], StringComparer.OrdinalIgnoreCase),
            ApplicationRoles.Viewer);

        Assert.Equal(AccessPreviewApplications.QualityAssurance, Assert.Single(applications).Id);
    }

    [Fact]
    public void Admin_console_preview_card_is_visible_only_for_admin_targets()
    {
        var permissions = new HashSet<string>(
            [QualityAssurancePermissions.ModuleView],
            StringComparer.OrdinalIgnoreCase);

        var sales = AdminAccessPreviewEndpoints.ApplicationsForAccess(
            BuildRegistry(), permissions, role: ApplicationRoles.Viewer);
        var administrator = AdminAccessPreviewEndpoints.ApplicationsForAccess(
            BuildRegistry(), permissions, role: ApplicationRoles.Admin);

        Assert.DoesNotContain(sales, application => application.Id == ApplicationRegistry.AdminConsoleApplicationId);
        Assert.Contains(administrator, application => application.Id == ApplicationRegistry.AdminConsoleApplicationId);
    }

    [Fact]
    public void User_overview_prepends_exact_unregistered_user_preview()
    {
        var targets = AdminAccessPreviewEndpoints.UserTargetsForOverview(
            [],
            BuildRegistry(),
            @"SON4L\administrator");

        var target = Assert.Single(targets);
        Assert.Equal("unregistered-user", target.Key);
        Assert.Equal(AccessPreviewTargetKinds.User, target.Kind);
        Assert.Equal("Unregistered user", target.Title);
        Assert.Equal("First-time Arda visitor", target.Subtitle);
        Assert.Equal(PortalAccountStatus.PendingSetup, target.AccountStatus);
        Assert.Null(target.Role);
        Assert.Empty(target.Applications);
    }

    [Fact]
    public void Registered_user_without_effective_entry_access_is_pending_setup()
    {
        var target = AdminAccessPreviewEndpoints.ToUserTarget(
            new PortalRoleRecord
            {
                Id = 17,
                AccountName = @"SON4L\pending.user",
                DisplayName = "Pending User",
                Role = ApplicationRoles.Viewer,
                IsActive = true
            },
            BuildRegistry());

        Assert.Equal(PortalAccountStatus.PendingSetup, target.AccountStatus);
        Assert.Null(target.Role);
        Assert.Empty(target.Applications);
    }

    [Fact]
    public void Registered_user_with_project_tracker_entry_access_is_configured()
    {
        var target = AdminAccessPreviewEndpoints.ToUserTarget(
            new PortalRoleRecord
            {
                Id = 18,
                AccountName = @"SON4L\tracker.viewer",
                DisplayName = "Tracker Viewer",
                Role = ApplicationRoles.Viewer,
                IsActive = true,
                ProjectTrackerGroupMemberships =
                [
                    new PortalProjectTrackerMembershipRecord
                    {
                        Group = new PortalProjectTrackerGroupRecord
                        {
                            Permissions =
                            [
                                new PortalProjectTrackerPermissionRecord
                                {
                                    PermissionKey = ApplicationPermissions.ModuleView
                                }
                            ]
                        }
                    }
                ]
            },
            BuildRegistry());

        Assert.Equal(PortalAccountStatus.Configured, target.AccountStatus);
        Assert.Equal(ApplicationRoles.Viewer, target.Role);
        Assert.Equal(AccessPreviewApplications.ProjectTracker, Assert.Single(target.Applications).Id);
    }

    private static ApplicationRegistry BuildRegistry()
    {
        const string json = """
        {
          "Portal": {
            "Applications": [
              { "Id": "project-tracker", "Name": "Project Tracker", "Order": 10, "Status": "Active", "AllowedRoles": [] },
              { "Id": "engineering-hub", "Name": "Engineering Hub", "Order": 20, "Status": "Active", "AllowedRoles": [] },
              { "Id": "estimating-dashboard", "Name": "Estimating Dashboard", "Order": 30, "Status": "Active", "AllowedRoles": [] },
              { "Id": "quality-assurance", "Name": "Quality Assurance", "Order": 40, "Status": "Active", "AllowedRoles": [] },
              { "Id": "admin-console", "Name": "Admin Console", "Order": 50, "Status": "Active", "AllowedRoles": ["Admin"] }
            ]
          }
        }
        """;
        var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        var configuration = new ConfigurationBuilder().AddJsonStream(stream).Build();
        return new ApplicationRegistry(configuration);
    }
}
