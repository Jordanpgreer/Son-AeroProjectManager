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
