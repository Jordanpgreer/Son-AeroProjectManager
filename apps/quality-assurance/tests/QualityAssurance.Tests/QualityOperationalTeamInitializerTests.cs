using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using QualityAssurance.Api.Data;
using QualityAssurance.Api.Models;
using QualityAssurance.Api.Services;
using SonAero.Platform.Security;

namespace QualityAssurance.Tests;

public sealed class QualityOperationalTeamInitializerTests
{
    [Fact]
    public async Task InitializeAsync_creates_the_required_shipping_queue_once()
    {
        await using var fixture = await InitializerFixture.CreateAsync();

        Assert.True(await fixture.Initializer.InitializeAsync());
        Assert.False(await fixture.Initializer.InitializeAsync());

        var team = Assert.Single(await fixture.Db.Teams.AsNoTracking().ToListAsync());
        Assert.Equal(ApplicationGroups.Shipper, team.Name);
        Assert.True(team.IsActive);
        Assert.Equal(1, team.Version);
        Assert.Empty(await fixture.Db.TeamMembers.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task InitializeAsync_preserves_an_existing_inactive_queue_for_admin_review()
    {
        await using var fixture = await InitializerFixture.CreateAsync();
        fixture.Db.Teams.Add(new QualityTeam
        {
            Name = ApplicationGroups.Shipper,
            IsActive = false,
            Version = 4,
            CreatedBy = "TEST\\admin",
            UpdatedBy = "TEST\\admin"
        });
        await fixture.Db.SaveChangesAsync();

        Assert.False(await fixture.Initializer.InitializeAsync());

        var team = Assert.Single(await fixture.Db.Teams.AsNoTracking().ToListAsync());
        Assert.False(team.IsActive);
        Assert.Equal(4, team.Version);
        Assert.Equal("TEST\\admin", team.UpdatedBy);
    }

    private sealed class InitializerFixture : IAsyncDisposable
    {
        private readonly SqliteConnection connection;

        private InitializerFixture(SqliteConnection connection, QualityAssuranceDbContext db)
        {
            this.connection = connection;
            Db = db;
            Initializer = new QualityOperationalTeamInitializer(
                db,
                new ConfigurationBuilder().Build(),
                NullLogger<QualityOperationalTeamInitializer>.Instance);
        }

        public QualityAssuranceDbContext Db { get; }
        public QualityOperationalTeamInitializer Initializer { get; }

        public static async Task<InitializerFixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var db = new QualityAssuranceDbContext(new DbContextOptionsBuilder<QualityAssuranceDbContext>()
                .UseSqlite(connection)
                .Options);
            await db.Database.EnsureCreatedAsync();
            return new InitializerFixture(connection, db);
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await connection.DisposeAsync();
        }
    }
}
