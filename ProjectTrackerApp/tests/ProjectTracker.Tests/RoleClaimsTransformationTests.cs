using System.Security.Claims;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using ProjectTracker.Api.Auth;
using ProjectTracker.Api.Data;
using ProjectTracker.Api.Models;

namespace ProjectTracker.Tests;

public sealed class RoleClaimsTransformationTests
{
    [Fact]
    public async Task TransformAsync_UsesStoredEditorRoleBeforeConfiguration()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<ProjectTrackerDbContext>().UseSqlite(connection).Options;
        await using var db = new ProjectTrackerDbContext(options);
        await db.Database.EnsureCreatedAsync();
        db.Users.Add(new AppUser
        {
            AccountName = "DOMAIN\\planner.one",
            DisplayName = "Planner One",
            Role = "Editor"
        });
        await db.SaveChangesAsync();

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Security:Admins:0"] = "DOMAIN\\planner.one"
            })
            .Build();
        var principal = AuthenticatedPrincipal("DOMAIN\\planner.one");

        await new RoleClaimsTransformation(configuration, db).TransformAsync(principal);

        Assert.True(principal.IsInRole("Editor"));
        Assert.True(principal.IsInRole("Viewer"));
        Assert.False(principal.IsInRole("Admin"));
    }

    [Fact]
    public async Task TransformAsync_UsesConfiguredAdminForFirstLogin()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<ProjectTrackerDbContext>().UseSqlite(connection).Options;
        await using var db = new ProjectTrackerDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Security:Admins:0"] = "DOMAIN\\new.admin"
            })
            .Build();
        var principal = AuthenticatedPrincipal("DOMAIN\\new.admin");

        await new RoleClaimsTransformation(configuration, db).TransformAsync(principal);

        Assert.True(principal.IsInRole("Admin"));
        Assert.True(principal.IsInRole("Editor"));
        Assert.True(principal.IsInRole("Viewer"));
    }

    private static ClaimsPrincipal AuthenticatedPrincipal(string accountName) =>
        new(new ClaimsIdentity([new Claim(ClaimTypes.Name, accountName)], "Test"));
}
