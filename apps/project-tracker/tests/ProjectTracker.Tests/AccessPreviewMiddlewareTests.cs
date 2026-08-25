using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using ProjectTracker.Api.Auth;
using ProjectTracker.Api.Data;
using SonAero.Platform.Security;

namespace ProjectTracker.Tests;

public sealed class AccessPreviewMiddlewareTests
{
    [Fact]
    public async Task Access_preview_prefers_permanent_portal_origin_for_admin_return()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<ProjectTrackerDbContext>().UseSqlite(connection).Options;
        await using var db = new ProjectTrackerDbContext(options);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Cors:HubOrigins:0"] = "https://hub.son4l.local",
                ["Cors:HubOrigins:1"] = "http://SON-IIS2:5140"
            })
            .Build();
        var service = new ProjectTrackerAccessPreviewService(db, configuration);
        var request = new DefaultHttpContext().Request;

        var url = service.HubAccessAdminUrl(request);

        Assert.Equal("https://hub.son4l.local/#/admin/access", url);
    }

    [Fact]
    public async Task Walkthrough_preview_returns_to_the_onboarding_admin_section()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<ProjectTrackerDbContext>().UseSqlite(connection).Options;
        await using var db = new ProjectTrackerDbContext(options);
        var context = new DefaultHttpContext();
        context.Request.Host = new HostString("localhost", 5135);

        var url = new ProjectTrackerAccessPreviewService(db, new ConfigurationBuilder().Build())
            .HubWalkthroughAdminUrl(context.Request);

        Assert.Equal("http://localhost:5140/#/admin/project-tracker/walkthrough", url);
    }

    [Fact]
    public async Task Access_preview_prefers_permanent_portal_origin_when_legacy_origin_is_first()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<ProjectTrackerDbContext>().UseSqlite(connection).Options;
        await using var db = new ProjectTrackerDbContext(options);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Cors:HubOrigins:0"] = "http://SON-IIS2:5140",
                ["Cors:HubOrigins:1"] = "https://hub.son4l.local"
            })
            .Build();
        var service = new ProjectTrackerAccessPreviewService(db, configuration);

        var url = service.HubAccessAdminUrl(new DefaultHttpContext().Request);

        Assert.Equal("https://hub.son4l.local/#/admin/access", url);
    }

    [Fact]
    public async Task Permanent_module_request_overrides_a_preserved_legacy_portal_origin()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<ProjectTrackerDbContext>().UseSqlite(connection).Options;
        await using var db = new ProjectTrackerDbContext(options);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Cors:HubOrigins:0"] = "http://SON-IIS2:5140"
            })
            .Build();
        var context = new DefaultHttpContext();
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("projects.hub.son4l.local");

        var url = new ProjectTrackerAccessPreviewService(db, configuration)
            .HubAccessAdminUrl(context.Request);

        Assert.Equal("https://hub.son4l.local/#/admin/access", url);
    }

    [Fact]
    public async Task Legacy_module_request_keeps_legacy_portal_origin_for_rollback()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<ProjectTrackerDbContext>().UseSqlite(connection).Options;
        await using var db = new ProjectTrackerDbContext(options);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Cors:HubOrigins:0"] = "http://SON-IIS2:5140"
            })
            .Build();
        var context = new DefaultHttpContext();
        context.Request.Scheme = "http";
        context.Request.Host = new HostString("SON-IIS2", 5135);

        var url = new ProjectTrackerAccessPreviewService(db, configuration)
            .HubAccessAdminUrl(context.Request);

        Assert.Equal("http://son-iis2:5140/#/admin/access", url, ignoreCase: true);
    }

    [Fact]
    public async Task Https_pilot_request_uses_pilot_port_even_when_permanent_cors_origin_is_first()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<ProjectTrackerDbContext>().UseSqlite(connection).Options;
        await using var db = new ProjectTrackerDbContext(options);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Cors:HubOrigins:0"] = "https://hub.son4l.local",
                ["Cors:HubOrigins:1"] = "http://SON-IIS2:5140"
            })
            .Build();
        var context = new DefaultHttpContext();
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("SON-IIS2", 6135);

        var url = new ProjectTrackerAccessPreviewService(db, configuration)
            .HubAccessAdminUrl(context.Request);

        Assert.Equal("https://son-iis2:6140/#/admin/access", url, ignoreCase: true);
    }

    [Fact]
    public async Task Https_local_request_uses_http_development_hub()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<ProjectTrackerDbContext>().UseSqlite(connection).Options;
        await using var db = new ProjectTrackerDbContext(options);
        var context = new DefaultHttpContext();
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("localhost", 7238);

        var url = new ProjectTrackerAccessPreviewService(db, new ConfigurationBuilder().Build())
            .HubAccessAdminUrl(context.Request);

        Assert.Equal("http://localhost:5140/#/admin/access", url);
    }

    [Fact]
    public async Task Arbitrary_request_host_is_never_used_in_preview_return_url()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<ProjectTrackerDbContext>().UseSqlite(connection).Options;
        await using var db = new ProjectTrackerDbContext(options);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Cors:HubOrigins:0"] = "https://hub.son4l.local"
            })
            .Build();
        var context = new DefaultHttpContext();
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("projects.hub.son4l.local.attacker.example");

        var url = new ProjectTrackerAccessPreviewService(db, configuration)
            .HubAccessAdminUrl(context.Request);

        Assert.Equal("https://hub.son4l.local/#/admin/access", url);
        Assert.Throws<InvalidOperationException>(() =>
            new ProjectTrackerAccessPreviewService(db, new ConfigurationBuilder().Build())
                .HubAccessAdminUrl(context.Request));
    }

    [Fact]
    public async Task InvokeAsync_BlocksUnsafeRequestsWhilePreviewIsActive()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<ProjectTrackerDbContext>().UseSqlite(connection).Options;
        await using var db = new ProjectTrackerDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var nextCalled = false;
        var middleware = new AccessPreviewMiddleware(_ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Post;
        context.Request.Path = "/api/projects";
        context.Response.Body = new MemoryStream();
        context.User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(AccessPreviewClaimTypes.Active, "true")],
            "Test"));

        await middleware.InvokeAsync(
            context,
            new ProjectTrackerAccessPreviewService(db, new ConfigurationBuilder().Build()));

        Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
        Assert.False(nextCalled);
    }
}
