using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Portal.Api.Data;
using Portal.Api.Services;

namespace Portal.Tests;

public sealed class PortalRoleStoreTests
{
    [Fact]
    public async Task FindRoleAsync_ReadsTrackerUserRoleCaseInsensitively()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<PortalRoleDbContext>().UseSqlite(connection).Options;
        await using var db = new PortalRoleDbContext(options);
        await db.Database.EnsureCreatedAsync();
        db.Users.Add(new PortalRoleRecord
        {
            AccountName = "SONAERO\\Planner.One",
            DisplayName = "Planner One",
            Role = "Editor"
        });
        await db.SaveChangesAsync();

        var store = new PortalRoleStore(db, NullLogger<PortalRoleStore>.Instance);

        Assert.Equal("Editor", await store.FindRoleAsync("sonaero\\planner.one"));
    }
}
