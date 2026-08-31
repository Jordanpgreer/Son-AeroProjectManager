using System.Security.Claims;
using EngineeringHub.Api.Auth;
using EngineeringHub.Api.Data;
using EngineeringHub.Api.Endpoints;
using EngineeringHub.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
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
    [Fact]
    public async Task Access_preview_returns_to_configured_permanent_portal_url()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<EngineeringRoleDbContext>().UseSqlite(connection).Options;
        await using var db = new EngineeringRoleDbContext(options);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Portal:Url"] = "https://hub.son4l.local"
            })
            .Build();
        var service = new EngineeringAccessPreviewService(db, configuration);

        var url = service.GetReturnToAdminUrl(new DefaultHttpContext());

        Assert.Equal("https://hub.son4l.local/#/admin/access", url);
    }

    [Fact]
    public async Task Permanent_module_request_overrides_a_preserved_legacy_portal_url()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<EngineeringRoleDbContext>().UseSqlite(connection).Options;
        await using var db = new EngineeringRoleDbContext(options);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Portal:Url"] = "http://SON-IIS2:5140"
            })
            .Build();
        var context = new DefaultHttpContext();
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("engineering.hub.son4l.local");

        var url = new EngineeringAccessPreviewService(db, configuration).GetReturnToAdminUrl(context);

        Assert.Equal("https://hub.son4l.local/#/admin/access", url);
    }

    [Fact]
    public async Task Legacy_module_request_keeps_legacy_portal_url_for_rollback()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<EngineeringRoleDbContext>().UseSqlite(connection).Options;
        await using var db = new EngineeringRoleDbContext(options);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Portal:Url"] = "http://SON-IIS2:5140"
            })
            .Build();
        var context = new DefaultHttpContext();
        context.Request.Scheme = "http";
        context.Request.Host = new HostString("SON-IIS2", 5150);

        var url = new EngineeringAccessPreviewService(db, configuration).GetReturnToAdminUrl(context);

        Assert.Equal("http://son-iis2:5140/#/admin/access", url, ignoreCase: true);
    }

    [Fact]
    public async Task Https_pilot_request_uses_pilot_port_even_with_permanent_portal_configuration()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<EngineeringRoleDbContext>().UseSqlite(connection).Options;
        await using var db = new EngineeringRoleDbContext(options);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Portal:Url"] = "https://hub.son4l.local"
            })
            .Build();
        var context = new DefaultHttpContext();
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("SON-IIS2", 6150);

        var url = new EngineeringAccessPreviewService(db, configuration).GetReturnToAdminUrl(context);

        Assert.Equal("https://son-iis2:6140/#/admin/access", url, ignoreCase: true);
    }

    [Fact]
    public async Task Arbitrary_request_host_is_never_used_in_preview_return_url()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<EngineeringRoleDbContext>().UseSqlite(connection).Options;
        await using var db = new EngineeringRoleDbContext(options);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Portal:Url"] = "https://hub.son4l.local"
            })
            .Build();
        var context = new DefaultHttpContext();
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("engineering.hub.son4l.local.attacker.example");

        var url = new EngineeringAccessPreviewService(db, configuration).GetReturnToAdminUrl(context);

        Assert.Equal("https://hub.son4l.local/#/admin/access", url);
        Assert.Throws<InvalidOperationException>(() =>
            new EngineeringAccessPreviewService(db, new ConfigurationBuilder().Build())
                .GetReturnToAdminUrl(context));
    }

    [Theory]
    [InlineData(ApplicationRoles.Viewer, false, false, false)]
    [InlineData(ApplicationRoles.Editor, true, true, false)]
    [InlineData(ApplicationRoles.Admin, true, true, true)]
    public void Module_roles_map_to_expected_permissions(
        string role,
        bool canViewInternalRevisions,
        bool canEditSpecifications,
        bool canManageSettings)
    {
        var permissions = EngineeringAuthorization.PermissionsForRole(role);

        Assert.Contains(EngineeringAuthorization.ReadPermission, permissions);
        Assert.Equal(canViewInternalRevisions, permissions.Contains(EngineeringPermissions.PendingRevisionsView));
        Assert.Equal(canEditSpecifications, permissions.Contains(EngineeringPermissions.SpecificationsEdit));
        Assert.DoesNotContain(EngineeringPermissions.SettingsManageGroups, permissions);
        Assert.Equal(canManageSettings, permissions.Contains(EngineeringPermissions.SettingsManageStorage));
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
            [EngineeringPermissions.ModuleView, EngineeringPermissions.SettingsManageStorage]));
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

    [Theory]
    [InlineData("Jordan Renamed", "Jordan Renamed")]
    [InlineData("   ", "Jordan Greer")]
    public void AttachAccess_ReplacesUntrustedDisplayNameWithTrustedValue(
        string storedDisplayName,
        string expectedDisplayName)
    {
        var service = new EngineeringUserService(
            new ConfigurationBuilder().Build(),
            new StubRoleStore(null));
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.Name, "SONAERO\\jordan.greer"),
                new Claim(EngineeringAuthorization.DisplayNameClaimType, "Spoofed Name")
            ],
            "Test"));
        var access = new EngineeringModuleAccess(
            ApplicationRoles.Editor,
            true,
            [EngineeringPermissions.ModuleView],
            ["Engineering"],
            "SONAERO\\jordan.greer",
            storedDisplayName);

        var attached = service.AttachAccess(principal, access);

        var displayName = Assert.Single(attached.Claims.Where(claim =>
            claim.Type == EngineeringAuthorization.DisplayNameClaimType));
        Assert.Equal(expectedDisplayName, displayName.Value);
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
    public async Task Preview_redeems_once_and_uses_the_target_users_live_engineering_access()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<EngineeringRoleDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new EngineeringRoleDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var administrator = new EngineeringUserRecord
        {
            AccountName = "SONAERO\\administrator",
            DisplayName = "Administrator",
            IsActive = true
        };
        db.Users.Add(administrator);
        db.Entry(administrator).Property("Role").CurrentValue = ApplicationRoles.Admin;

        var target = new EngineeringUserRecord
        {
            AccountName = "SONAERO\\viewer",
            DisplayName = "Engineering Viewer",
            IsActive = true,
            ModuleAccessAssignments =
            [
                new EngineeringModuleAccessRecord
                {
                    ModuleKey = EngineeringAuthorization.ModuleKey,
                    Role = ApplicationRoles.Viewer
                }
            ]
        };
        var group = new EngineeringAccessGroupRecord
        {
            Name = "Drawing Readers",
            Permissions =
            [
                new EngineeringGroupPermissionRecord { PermissionKey = EngineeringPermissions.ModuleView },
                new EngineeringGroupPermissionRecord { PermissionKey = EngineeringPermissions.DrawingsView }
            ]
        };
        target.GroupMemberships.Add(new EngineeringUserGroupMembershipRecord { Group = group });
        db.Users.Add(target);
        await db.SaveChangesAsync();

        var token = AccessPreviewTokens.Create();
        db.AccessPreviewSessions.Add(new AccessPreviewSessionRecord
        {
            Id = Guid.NewGuid(),
            TokenHash = AccessPreviewTokens.Hash(token),
            AdministratorAccountName = administrator.AccountName,
            TargetKey = $"{AccessPreviewTargetKinds.User}:{target.Id}",
            ApplicationId = AccessPreviewApplications.Engineering,
            IssuedAt = DateTimeOffset.UtcNow,
            LaunchExpiresAt = DateTimeOffset.UtcNow.AddMinutes(2),
            SessionExpiresAt = DateTimeOffset.UtcNow.AddMinutes(10)
        });
        await db.SaveChangesAsync();

        var service = new EngineeringAccessPreviewService(db, new ConfigurationBuilder().Build());
        var start = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.Name, administrator.AccountName)], "Test"))
        };
        var first = await service.StartAsync(start, token);
        var second = await service.StartAsync(start, token);

        Assert.True(first.Succeeded);
        Assert.False(second.Succeeded);
        Assert.Contains(EngineeringAccessPreviewService.CookieName, start.Response.Headers.SetCookie.ToString());

        var request = new DefaultHttpContext
        {
            User = start.User
        };
        request.Request.Headers.Cookie = $"{EngineeringAccessPreviewService.CookieName}={token}";
        var access = await service.ResolveActiveAsync(request);

        Assert.NotNull(access);
        Assert.True(access.IsPreview);
        Assert.Equal("SONAERO\\viewer", access.AccountName);
        Assert.Equal("SONAERO\\administrator", access.PreviewActorAccountName);
        Assert.Contains(EngineeringPermissions.DrawingsView, access.Permissions);
        Assert.DoesNotContain(EngineeringPermissions.DrawingCreate, access.Permissions);
    }

    [Fact]
    public void Drawing_routes_enforce_granular_permission_policies()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddDbContext<EngineeringDbContext>(options =>
            options.UseSqlite("Data Source=:memory:"));
        builder.Services.AddScoped<IDrawingFileStore, DrawingFileStore>();
        builder.Services.AddScoped<MylarCustodyService>();
        builder.Services.AddSingleton<ToolCatalogReviewStore>();
        builder.Services.AddScoped<ToolCatalogWorkbookService>();
        var app = builder.Build();
        var api = app.MapGroup("/api")
            .RequireAuthorization(EngineeringAuthorization.ReadPolicy);
        api.MapDrawingEndpoints();
        api.MapDrawingOperationalEndpoints();
        api.MapToolingEndpoints();

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
            ("PUT", "/api/tools/{id:int}/archive", EngineeringPermissions.ToolingArchiveManage),
            ("POST", "/api/tools/{id:int}/checkout", EngineeringPermissions.ToolingCustodyManage),
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
