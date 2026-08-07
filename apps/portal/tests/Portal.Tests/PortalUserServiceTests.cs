using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Portal.Api.Services;

namespace Portal.Tests;

public sealed class PortalUserServiceTests
{
    private sealed class StubRoleStore(
        string? role = null,
        IReadOnlyDictionary<string, string>? moduleRoles = null) : IPortalRoleStore
    {
        public Task<string?> FindRoleAsync(string accountName, CancellationToken cancellationToken = default)
            => Task.FromResult(role);

        public Task<IReadOnlyDictionary<string, string>> FindModuleRolesAsync(
            string accountName,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(moduleRoles
                ?? (IReadOnlyDictionary<string, string>)new Dictionary<string, string>());
    }

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
        var service = new PortalUserService(new HttpContextAccessor { HttpContext = null }, configuration, new StubRoleStore());

        var me = await service.CurrentAsync();

        Assert.Equal("SONAERO\\jane.doe", me.AccountName);
        Assert.Equal("Jane Doe", me.DisplayName);
        Assert.Equal("Editor", me.Role);
        Assert.All(me.Modules, module => Assert.Equal("Editor", module.Role));
        Assert.DoesNotContain(me.Modules, module => module.ModuleKey == "quality-assurance");
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
            new StubRoleStore(
                "Viewer",
                new Dictionary<string, string> { ["estimating"] = "Editor" }));

        var me = await service.CurrentAsync();

        var module = Assert.Single(me.Modules);
        Assert.Equal("estimating", module.ModuleKey);
        Assert.Equal("Editor", module.Role);
        Assert.Contains("estimating.quotes.manage", module.Permissions);
    }

    [Fact]
    public async Task Current_IgnoresUnsupportedModuleRoleAssignments()
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
            new StubRoleStore(
                "Viewer",
                new Dictionary<string, string> { ["quality-assurance"] = "Viewer" }));

        Assert.Empty((await service.CurrentAsync()).Modules);
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
        var service = new PortalUserService(new HttpContextAccessor { HttpContext = httpContext }, configuration, new StubRoleStore());

        var me = await service.CurrentAsync();

        Assert.Equal("SONAERO\\lead.planner", me.AccountName);
        Assert.Equal("Admin", me.Role);
    }

    [Fact]
    public async Task Current_WindowsMode_UnknownAccountDefaultsToViewer()
    {
        var configuration = BuildConfiguration("""
        { "Authentication": { "Mode": "Windows" }, "Portal": {} }
        """);
        var httpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                new[] { new Claim(ClaimTypes.Name, "SONAERO\\random.user") }, "TestAuth")),
        };
        var service = new PortalUserService(new HttpContextAccessor { HttpContext = httpContext }, configuration, new StubRoleStore());

        Assert.Equal("Viewer", (await service.CurrentAsync()).Role);
    }

    [Fact]
    public async Task Current_WindowsMode_PrefersSharedRoleStoreOverBootstrapConfiguration()
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
            new StubRoleStore("Viewer"));

        Assert.Equal("Viewer", (await service.CurrentAsync()).Role);
    }
}
