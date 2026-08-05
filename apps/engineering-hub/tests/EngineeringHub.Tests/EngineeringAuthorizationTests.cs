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
    [InlineData(ApplicationRoles.Viewer, false, false, false)]
    [InlineData(ApplicationRoles.Editor, true, true, false)]
    [InlineData(ApplicationRoles.Admin, true, true, true)]
    public void Module_roles_map_to_expected_permissions(
        string role,
        bool canViewInternalRevisions,
        bool canEditSpecifications,
        bool canManageAccess)
    {
        var permissions = EngineeringAuthorization.PermissionsForRole(role);

        Assert.Contains(EngineeringAuthorization.ReadPermission, permissions);
        Assert.Equal(canViewInternalRevisions, permissions.Contains(EngineeringPermissions.PendingRevisionsView));
        Assert.Equal(canEditSpecifications, permissions.Contains(EngineeringPermissions.SpecificationsEdit));
        Assert.Equal(canManageAccess, permissions.Contains(EngineeringPermissions.SettingsManageGroups));
        Assert.Equal(canManageAccess, permissions.Contains(EngineeringPermissions.SettingsManageStorage));
    }

    [Fact]
    public void Shared_permission_catalog_infers_legacy_module_role()
    {
        Assert.Null(EngineeringPermissions.RoleFor([]));
        Assert.Equal(ApplicationRoles.Viewer, EngineeringPermissions.RoleFor(
            [EngineeringPermissions.ModuleView, EngineeringPermissions.DrawingsView]));
        Assert.Equal(ApplicationRoles.Editor, EngineeringPermissions.RoleFor(
            [EngineeringPermissions.ModuleView, EngineeringPermissions.DrawingCreate]));
        Assert.Equal(ApplicationRoles.Admin, EngineeringPermissions.RoleFor(
            [EngineeringPermissions.ModuleView, EngineeringPermissions.SettingsManageGroups]));
        Assert.Contains(
            EngineeringPermissions.SettingsView,
            EngineeringPermissions.Expand([EngineeringPermissions.SettingsManageStorage]));
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
        var group = new EngineeringAccessGroupRecord
        {
            Name = "Engineering",
            Permissions = EngineeringAuthorization.PermissionsForRole(ApplicationRoles.Editor)
                .Select(permission => new EngineeringGroupPermissionRecord { PermissionKey = permission })
                .ToList()
        };
        db.Groups.Add(group);
        user.GroupMemberships.Add(new EngineeringUserGroupMembershipRecord { Group = group });
        await db.SaveChangesAsync();

        var store = new EngineeringRoleStore(db, NullLogger<EngineeringRoleStore>.Instance);
        var access = await store.FindAccessAsync("sonaero/EDITOR");

        Assert.NotNull(access);
        Assert.True(access.IsEnabled);
        Assert.Equal(ApplicationRoles.Editor, access.Role);
        Assert.Contains(EngineeringPermissions.SpecificationsView, access.Permissions);
        Assert.Equal(["Engineering"], access.Groups);
    }

    [Fact]
    public async Task Registered_user_without_module_view_permission_is_denied()
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
        var group = new EngineeringAccessGroupRecord
        {
            Name = "Restricted",
            Permissions = [new EngineeringGroupPermissionRecord { PermissionKey = EngineeringPermissions.DrawingsView }]
        };
        db.Groups.Add(group);
        user.GroupMemberships.Add(new EngineeringUserGroupMembershipRecord { Group = group });
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
            new StubRoleStore(new EngineeringModuleAccess(
                ApplicationRoles.Editor,
                true,
                EngineeringAuthorization.PermissionsForRole(ApplicationRoles.Editor),
                ["Engineering"])));
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.Name, "SONAERO\\engineering.editor")],
            "Test"));

        var me = await service.CurrentAsync(principal);

        Assert.Equal(ApplicationRoles.Editor, me.Role);
        Assert.Contains(EngineeringAuthorization.ReadPermission, me.Permissions);
        Assert.Contains(EngineeringPermissions.SpecificationsEdit, me.Permissions);
        Assert.DoesNotContain(EngineeringPermissions.SettingsManageGroups, me.Permissions);
        Assert.Equal(["Engineering"], me.Groups);
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
            new StubRoleStore(new EngineeringModuleAccess(
                ApplicationRoles.Viewer,
                true,
                EngineeringAuthorization.PermissionsForRole(ApplicationRoles.Viewer),
                ["View Only"])));

        var access = await service.ResolveAccessAsync("DEV\\viewer");

        Assert.NotNull(access);
        Assert.Equal(ApplicationRoles.Viewer, access.Role);
    }

    [Fact]
    public void Drawing_routes_enforce_granular_permission_policies()
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

        AssertPolicies(app, "GET", "/api/drawings", EngineeringAuthorization.ReadPolicy, EngineeringPermissions.DrawingsView);
        var protectedRoutes = new (string Method, string Route, string Permission)[]
        {
            ("POST", "/api/drawings", EngineeringPermissions.DrawingCreate),
            ("POST", "/api/drawings/create-with-revision", EngineeringPermissions.DrawingCreate),
            ("POST", "/api/drawings/{id:int}/archive", EngineeringPermissions.DrawingArchive),
            ("POST", "/api/drawings/{id:int}/revisions", EngineeringPermissions.RevisionCreate),
            ("POST", "/api/drawing-revisions/{id:int}/editable-draft", EngineeringPermissions.RevisionEdit),
            ("POST", "/api/drawing-revisions/{id:int}/approve", EngineeringPermissions.RevisionApprove),
            ("POST", "/api/drawing-revisions/{id:int}/make-current", EngineeringPermissions.RevisionMakeCurrent),
            ("POST", "/api/drawings/{id:int}/mylars", EngineeringPermissions.MylarManage),
            ("POST", "/api/drawings/{id:int}/validations", EngineeringPermissions.ValidationsManage),
            ("GET", "/api/drawing-documents/{id:int}/file", EngineeringPermissions.SupportingDocumentsView)
        };
        foreach (var (method, route, permission) in protectedRoutes)
        {
            AssertPolicies(
                app,
                method,
                route,
                EngineeringAuthorization.ReadPolicy,
                permission);
        }

        AssertPolicies(app, "DELETE", "/api/drawings/{id:int}", EngineeringAuthorization.ReadPolicy, EngineeringPermissions.DrawingDelete);
        AssertPolicies(app, "DELETE", "/api/drawing-revisions/{id:int}", EngineeringAuthorization.ReadPolicy, EngineeringPermissions.RevisionDelete);
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
