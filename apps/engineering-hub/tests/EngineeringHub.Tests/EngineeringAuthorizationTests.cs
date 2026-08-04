using System.Security.Claims;
using EngineeringHub.Api.Auth;
using EngineeringHub.Api.Data;
using EngineeringHub.Api.Endpoints;
using EngineeringHub.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SonAero.Platform.Security;
using Xunit;

namespace EngineeringHub.Tests;

public sealed class EngineeringAuthorizationTests
{
    [Theory]
    [InlineData(ApplicationRoles.Viewer, true, false, false)]
    [InlineData(ApplicationRoles.Editor, true, true, false)]
    [InlineData(ApplicationRoles.Admin, true, true, true)]
    public void Module_roles_map_to_expected_permissions(
        string role,
        bool canRead,
        bool canWrite,
        bool canAdmin)
    {
        var permissions = EngineeringAuthorization.PermissionsForRole(role);

        Assert.Equal(canRead, permissions.Contains(EngineeringAuthorization.ReadPermission));
        Assert.Equal(canWrite, permissions.Contains(EngineeringAuthorization.WritePermission));
        Assert.Equal(canAdmin, permissions.Contains(EngineeringAuthorization.AdminPermission));
    }

    [Fact]
    public async Task Role_store_reads_active_engineering_assignment_from_shared_contract()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<EngineeringRoleDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new EngineeringRoleDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var user = new EngineeringUserRecord
        {
            AccountName = "SONAERO\\editor",
            DisplayName = "Engineering Editor",
            IsActive = true
        };
        db.Users.Add(user);
        db.UserModuleAccess.Add(new EngineeringModuleAccessRecord
        {
            User = user,
            ModuleKey = ApplicationModules.Engineering,
            Role = ApplicationRoles.Editor
        });
        await db.SaveChangesAsync();

        var store = new EngineeringRoleStore(db, NullLogger<EngineeringRoleStore>.Instance);
        var access = await store.FindAccessAsync("sonaero/EDITOR");

        Assert.NotNull(access);
        Assert.True(access.IsEnabled);
        Assert.Equal(ApplicationRoles.Editor, access.Role);
    }

    [Fact]
    public async Task Null_role_assignment_disables_module_access()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<EngineeringRoleDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new EngineeringRoleDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var user = new EngineeringUserRecord
        {
            AccountName = "SONAERO\\disabled",
            DisplayName = "Disabled User",
            IsActive = true
        };
        db.Users.Add(user);
        db.UserModuleAccess.Add(new EngineeringModuleAccessRecord
        {
            User = user,
            ModuleKey = ApplicationModules.Engineering,
            Role = null
        });
        await db.SaveChangesAsync();

        var store = new EngineeringRoleStore(db, NullLogger<EngineeringRoleStore>.Instance);
        var access = await store.FindAccessAsync(user.AccountName);

        Assert.NotNull(access);
        Assert.False(access.IsEnabled);
    }

    [Fact]
    public async Task Me_returns_role_and_permissions_from_assignment()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Authentication:Mode"] = "Windows"
            })
            .Build();
        var service = new EngineeringUserService(
            configuration,
            new StubRoleStore(new EngineeringModuleAccess(ApplicationRoles.Editor, true)));
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.Name, "SONAERO\\engineering.editor")],
            "Test"));

        var me = await service.CurrentAsync(principal);

        Assert.Equal(ApplicationRoles.Editor, me.Role);
        Assert.Contains(EngineeringAuthorization.ReadPermission, me.Permissions);
        Assert.Contains(EngineeringAuthorization.WritePermission, me.Permissions);
        Assert.DoesNotContain(EngineeringAuthorization.AdminPermission, me.Permissions);
    }

    [Fact]
    public async Task Unassigned_windows_user_has_no_module_access()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Authentication:Mode"] = "Windows"
            })
            .Build();
        var service = new EngineeringUserService(configuration, new StubRoleStore(null));

        var access = await service.ResolveAccessAsync("SONAERO\\unassigned");

        Assert.Null(access);
    }

    [Fact]
    public async Task Development_mode_still_uses_shared_module_assignment()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Authentication:Mode"] = "Development",
                ["Engineering:DevelopmentRole"] = ApplicationRoles.Admin
            })
            .Build();
        var service = new EngineeringUserService(
            configuration,
            new StubRoleStore(new EngineeringModuleAccess(ApplicationRoles.Viewer, true)));

        var access = await service.ResolveAccessAsync("DEV\\viewer");

        Assert.NotNull(access);
        Assert.Equal(ApplicationRoles.Viewer, access.Role);
    }

    [Fact]
    public void Drawing_routes_enforce_read_write_and_admin_policies()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddDbContext<EngineeringDbContext>(options =>
            options.UseSqlite("Data Source=:memory:"));
        builder.Services.AddScoped<IDrawingFileStore, DrawingFileStore>();
        builder.Services.AddScoped<MylarCustodyService>();
        var app = builder.Build();
        var api = app.MapGroup("/api")
            .RequireAuthorization(EngineeringAuthorization.ReadPolicy);
        api.MapDrawingEndpoints();
        api.MapDrawingOperationalEndpoints();

        AssertPolicies(app, "GET", "/api/drawings", EngineeringAuthorization.ReadPolicy);
        var writeRoutes = new (string Method, string Route)[]
        {
            ("POST", "/api/drawings"),
            ("POST", "/api/drawings/create-with-revision"),
            ("PUT", "/api/drawings/{id:int}"),
            ("POST", "/api/drawings/{id:int}/archive"),
            ("POST", "/api/drawings/{id:int}/obsolete"),
            ("POST", "/api/drawings/{id:int}/revisions"),
            ("POST", "/api/drawing-revisions/{id:int}/editable-draft"),
            ("PUT", "/api/drawing-revisions/{id:int}/status"),
            ("POST", "/api/drawing-revisions/{id:int}/approve"),
            ("POST", "/api/drawing-revisions/{id:int}/make-current"),
            ("POST", "/api/drawings/{id:int}/mylars"),
            ("POST", "/api/drawings/{id:int}/mylars/{mylarId:int}/checkout"),
            ("POST", "/api/drawings/{id:int}/mylars/{mylarId:int}/checkin"),
            ("POST", "/api/drawings/{id:int}/validations")
        };
        foreach (var (method, route) in writeRoutes)
        {
            AssertPolicies(
                app,
                method,
                route,
                EngineeringAuthorization.ReadPolicy,
                EngineeringAuthorization.WritePolicy);
        }

        foreach (var route in new[]
                 {
                     "/api/drawings/{id:int}",
                     "/api/drawing-revisions/{id:int}"
                 })
        {
            AssertPolicies(
                app,
                "DELETE",
                route,
                EngineeringAuthorization.ReadPolicy,
                EngineeringAuthorization.AdminPolicy);
        }
    }

    private static void AssertPolicies(
        WebApplication app,
        string method,
        string route,
        params string[] expectedPolicies)
    {
        var endpoint = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Single(candidate =>
                candidate.RoutePattern.RawText == route
                && candidate.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods.Contains(method) == true);
        var policies = endpoint.Metadata
            .GetOrderedMetadata<IAuthorizeData>()
            .Select(data => data.Policy)
            .Where(policy => policy is not null)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var expectedPolicy in expectedPolicies)
        {
            Assert.Contains(expectedPolicy, policies);
        }
    }

    private sealed class StubRoleStore(EngineeringModuleAccess? access) : IEngineeringRoleStore
    {
        public Task<EngineeringModuleAccess?> FindAccessAsync(
            string accountName,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(access);
    }
}
