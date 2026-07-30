using EstimatingDashboard.Api.Auth;
using EstimatingDashboard.Api.Data;
using EstimatingDashboard.Api.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace EstimatingDashboard.Tests;

public sealed class EstimatingAccessStoreTests
{
    [Fact]
    public async Task FindsActiveUserWithExactEstimatingModuleRole()
    {
        await using var fixture = await AccessFixture.CreateAsync();
        fixture.Db.Users.Add(new EstimatingUserRecord
        {
            Id = 7,
            AccountName = "SONAERO\\estimator",
            DisplayName = "Estimator",
            IsActive = true,
            ModuleAccesses =
            [
                new EstimatingModuleAccessRecord
                {
                    AppUserId = 7,
                    ModuleKey = EstimatingModule.Key,
                    Role = EstimatingRoles.Editor
                }
            ]
        });
        await fixture.Db.SaveChangesAsync();

        var access = await fixture.Store.FindEnabledAsync("sonaero\\ESTIMATOR");

        Assert.NotNull(access);
        Assert.Equal(EstimatingRoles.Editor, access.Role);
        Assert.Contains(EstimatingPermissions.ManageQuotes, access.Permissions);
    }

    [Theory]
    [InlineData(false, EstimatingModule.Key, EstimatingRoles.Admin)]
    [InlineData(true, "engineering", EstimatingRoles.Admin)]
    [InlineData(true, EstimatingModule.Key, null)]
    [InlineData(true, EstimatingModule.Key, "Owner")]
    public async Task DeniesInactiveMissingOrInvalidModuleAccess(
        bool active,
        string moduleKey,
        string? role)
    {
        await using var fixture = await AccessFixture.CreateAsync();
        fixture.Db.Users.Add(new EstimatingUserRecord
        {
            Id = 8,
            AccountName = "SONAERO\\denied",
            DisplayName = "Denied",
            IsActive = active,
            ModuleAccesses =
            [
                new EstimatingModuleAccessRecord
                {
                    AppUserId = 8,
                    ModuleKey = moduleKey,
                    Role = role
                }
            ]
        });
        await fixture.Db.SaveChangesAsync();

        var access = await fixture.Store.FindEnabledAsync("SONAERO\\denied");

        Assert.Null(access);
    }

    private sealed class AccessFixture : IAsyncDisposable
    {
        private readonly SqliteConnection connection;

        private AccessFixture(
            SqliteConnection connection,
            EstimatingAccessDbContext db)
        {
            this.connection = connection;
            Db = db;
            Store = new EstimatingAccessStore(
                db,
                NullLogger<EstimatingAccessStore>.Instance);
        }

        public EstimatingAccessDbContext Db { get; }
        public EstimatingAccessStore Store { get; }

        public static async Task<AccessFixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<EstimatingAccessDbContext>()
                .UseSqlite(connection)
                .Options;
            var db = new EstimatingAccessDbContext(options);
            await db.Database.EnsureCreatedAsync();
            return new AccessFixture(connection, db);
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await connection.DisposeAsync();
        }
    }
}
