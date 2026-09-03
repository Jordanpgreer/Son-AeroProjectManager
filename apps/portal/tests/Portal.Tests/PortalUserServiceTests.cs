using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Portal.Api.Services;

namespace Portal.Tests;

public sealed class PortalUserServiceTests
{
    private sealed class StubRoleStore(PortalAccountLookup account) : IPortalRoleStore
    {
        public Task<PortalAccountLookup> FindAccountAsync(
            string accountName,
            CancellationToken cancellationToken = default) => Task.FromResult(account);
    }

    private static PortalAccountLookup Found(
        string role = "Viewer",
        IReadOnlyDictionary<string, string>? moduleRoles = null,
        string? displayName = null,
        bool hasProjectTrackerAccess = false,
        bool isActive = true) => new(
            PortalAccountLookupStatus.Found,
            isActive,
            role,
            displayName,
            hasProjectTrackerAccess,
            moduleRoles ?? new Dictionary<string, string>());

    private static IConfiguration BuildConfiguration(string json)
    {
        var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        return new ConfigurationBuilder().AddJsonStream(stream).Build();
    }

    [Fact]
    public async Task Current_DevelopmentMode_UsesConfiguredAccountRoleAndTidiesDisplayName()
    {
        var configuration = BuildConfiguration("""
        {
          "Authentication": { "Mode": "Development", "DevelopmentAccount": "SONAERO\\jane.doe" },
          "Portal": { "DevelopmentRole": "Editor" }
        }
        """);
        var service = new PortalUserService(
            new HttpContextAccessor { HttpContext = null },
            configuration,
            new StubRoleStore(PortalAccountLookup.Missing()));

        var me = await service.CurrentAsync();

        Assert.Equal("SONAERO\\jane.doe", me.AccountName);
        Assert.Equal("Jane Doe", me.DisplayName);
        Assert.Equal(Portal.Api.Dtos.PortalAccountStatus.Configured, me.AccountStatus);
        Assert.Equal("Editor", me.Role);
        Assert.All(me.Modules, module => Assert.Equal("Editor", module.Role));
        Assert.Contains(me.Modules, module =>
            module.ModuleKey == "quality-assurance"
            && module.Permissions.Contains("quality-assurance.shipments.create"));
    }

    [Fact]
    public async Task Current_WindowsMode_ReturnsOnlyExplicitModuleAssignments()
    {
        var configuration = BuildConfiguration("""
        { "Authentication": { "Mode": "Windows" }, "Portal": {} }
        """);
        var httpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                new[] { new Claim(ClaimTypes.Name, "SONAERO\\estimator") }, "TestAuth")),
        };
        var service = new PortalUserService(
            new HttpContextAccessor { HttpContext = httpContext },
            configuration,
            new StubRoleStore(Found(
                moduleRoles: new Dictionary<string, string> { ["estimating"] = "Editor" })));

        var me = await service.CurrentAsync();

        var module = Assert.Single(me.Modules);
        Assert.Equal("estimating", module.ModuleKey);
        Assert.Equal("Editor", module.Role);
        Assert.Contains("estimating.quotes.manage", module.Permissions);
    }

    [Fact]
    public async Task Current_WindowsMode_UsesAdministratorConfiguredDisplayName()
    {
        var configuration = BuildConfiguration("""
        { "Authentication": { "Mode": "Windows" }, "Portal": {} }
        """);
        var httpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                new[] { new Claim(ClaimTypes.Name, "SONAERO\\estimator") }, "TestAuth")),
        };
        var service = new PortalUserService(
            new HttpContextAccessor { HttpContext = httpContext },
            configuration,
            new StubRoleStore(Found(
                displayName: "Preferred Application Name",
                hasProjectTrackerAccess: true)));

        Assert.Equal("Preferred Application Name", (await service.CurrentAsync()).DisplayName);
    }

    [Fact]
    public async Task Current_SupportsQualityViewerAssignments()
    {
        var configuration = BuildConfiguration("""
        { "Authentication": { "Mode": "Windows" }, "Portal": {} }
        """);
        var httpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                new[] { new Claim(ClaimTypes.Name, "SONAERO\\qa.viewer") }, "TestAuth")),
        };
        var service = new PortalUserService(
            new HttpContextAccessor { HttpContext = httpContext },
            configuration,
            new StubRoleStore(Found(
                moduleRoles: new Dictionary<string, string> { ["quality-assurance"] = "Viewer" })));

        var module = Assert.Single((await service.CurrentAsync()).Modules);
        Assert.Equal("quality-assurance", module.ModuleKey);
        Assert.Equal("Viewer", module.Role);
        Assert.Contains("quality-assurance.shipments.view", module.Permissions);
    }

    [Fact]
    public async Task Current_WindowsMode_MapsConfiguredAdminAccountToAdminRole()
    {
        var configuration = BuildConfiguration("""
        {
          "Authentication": { "Mode": "Windows" },
          "Portal": { "Admins": [ "SONAERO\\lead.planner" ] }
        }
        """);
        var httpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                new[] { new Claim(ClaimTypes.Name, "SONAERO\\lead.planner") }, "TestAuth")),
        };
        var service = new PortalUserService(
            new HttpContextAccessor { HttpContext = httpContext },
            configuration,
            new StubRoleStore(PortalAccountLookup.Missing()));

        var me = await service.CurrentAsync();

        Assert.Equal("SONAERO\\lead.planner", me.AccountName);
        Assert.Equal(Portal.Api.Dtos.PortalAccountStatus.Configured, me.AccountStatus);
        Assert.Equal("Admin", me.Role);
    }

    [Fact]
    public async Task Current_WindowsMode_UnknownAccountIsPendingWithoutRoleOrModules()
    {
        var configuration = BuildConfiguration("""
        { "Authentication": { "Mode": "Windows" }, "Portal": {} }
        """);
        var httpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                new[] { new Claim(ClaimTypes.Name, "SONAERO\\random.user") }, "TestAuth")),
        };
        var service = new PortalUserService(
            new HttpContextAccessor { HttpContext = httpContext },
            configuration,
            new StubRoleStore(PortalAccountLookup.Missing()));

        var me = await service.CurrentAsync();
        Assert.Equal(Portal.Api.Dtos.PortalAccountStatus.PendingSetup, me.AccountStatus);
        Assert.Null(me.Role);
        Assert.Empty(me.Modules);
    }

    [Fact]
    public async Task Current_WindowsMode_ActiveAccountWithoutEffectiveAccessStaysPending()
    {
        var configuration = BuildConfiguration("""
        {
          "Authentication": { "Mode": "Windows" },
          "Portal": { "Admins": [ "SONAERO\\planner" ] }
        }
        """);
        var httpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                new[] { new Claim(ClaimTypes.Name, "SONAERO\\planner") }, "TestAuth")),
        };
        var service = new PortalUserService(
            new HttpContextAccessor { HttpContext = httpContext },
            configuration,
            new StubRoleStore(Found()));

        var me = await service.CurrentAsync();
        Assert.Equal(Portal.Api.Dtos.PortalAccountStatus.PendingSetup, me.AccountStatus);
        Assert.Null(me.Role);
        Assert.Empty(me.Modules);
    }

    [Fact]
    public async Task Current_WindowsMode_ProjectTrackerViewPermissionConfiguresViewer()
    {
        var configuration = BuildConfiguration("""
        { "Authentication": { "Mode": "Windows" }, "Portal": {} }
        """);
        var httpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                new[] { new Claim(ClaimTypes.Name, "SONAERO\\tracker.viewer") }, "TestAuth")),
        };
        var service = new PortalUserService(
            new HttpContextAccessor { HttpContext = httpContext },
            configuration,
            new StubRoleStore(Found(hasProjectTrackerAccess: true)));

        var me = await service.CurrentAsync();

        Assert.Equal(Portal.Api.Dtos.PortalAccountStatus.Configured, me.AccountStatus);
        Assert.Equal("Viewer", me.Role);
    }

    [Fact]
    public async Task Current_WindowsMode_InactiveAccountDoesNotUseBootstrapRole()
    {
        var configuration = BuildConfiguration("""
        {
          "Authentication": { "Mode": "Windows" },
          "Portal": { "Admins": [ "SONAERO\\disabled.admin" ] }
        }
        """);
        var httpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                new[] { new Claim(ClaimTypes.Name, "SONAERO\\disabled.admin") }, "TestAuth")),
        };
        var service = new PortalUserService(
            new HttpContextAccessor { HttpContext = httpContext },
            configuration,
            new StubRoleStore(Found(
                role: "Admin",
                hasProjectTrackerAccess: true,
                isActive: false)));

        var me = await service.CurrentAsync();

        Assert.Equal(Portal.Api.Dtos.PortalAccountStatus.Inactive, me.AccountStatus);
        Assert.Null(me.Role);
        Assert.Empty(me.Modules);
    }

    [Fact]
    public async Task Current_WindowsMode_UnavailableStoreIsDistinctFromPending()
    {
        var configuration = BuildConfiguration("""
        { "Authentication": { "Mode": "Windows" }, "Portal": {} }
        """);
        var httpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                new[] { new Claim(ClaimTypes.Name, "SONAERO\\ordinary.user") }, "TestAuth")),
        };
        var service = new PortalUserService(
            new HttpContextAccessor { HttpContext = httpContext },
            configuration,
            new StubRoleStore(PortalAccountLookup.Unavailable()));

        var me = await service.CurrentAsync();

        Assert.Equal(Portal.Api.Dtos.PortalAccountStatus.Unavailable, me.AccountStatus);
        Assert.Null(me.Role);
    }

    [Fact]
    public async Task Current_WindowsMode_BootstrapEditorRemainsConfiguredWhenStoreIsUnavailable()
    {
        var configuration = BuildConfiguration("""
        {
          "Authentication": { "Mode": "Windows" },
          "Portal": { "Editors": [ "SONAERO\\bootstrap.editor" ] }
        }
        """);
        var httpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                new[] { new Claim(ClaimTypes.Name, "SONAERO\\bootstrap.editor") }, "TestAuth")),
        };
        var service = new PortalUserService(
            new HttpContextAccessor { HttpContext = httpContext },
            configuration,
            new StubRoleStore(PortalAccountLookup.Unavailable()));

        var me = await service.CurrentAsync();

        Assert.Equal(Portal.Api.Dtos.PortalAccountStatus.Configured, me.AccountStatus);
        Assert.Equal("Editor", me.Role);
    }
}
