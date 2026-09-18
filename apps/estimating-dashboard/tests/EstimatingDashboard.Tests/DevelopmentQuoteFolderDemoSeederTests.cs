using EstimatingDashboard.Api.Auth;
using EstimatingDashboard.Api.Data;
using EstimatingDashboard.Api.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace EstimatingDashboard.Tests;

public sealed class DevelopmentQuoteFolderDemoSeederTests
{
    [Fact]
    public async Task Enabled_seed_is_idempotent_and_visible_to_the_development_estimator()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new EstimatingAccessDbContext(
            new DbContextOptionsBuilder<EstimatingAccessDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        var now = new DateTimeOffset(2026, 9, 18, 14, 30, 0, TimeSpan.Zero);
        var options = Options.Create(new DevelopmentQuoteFolderDemoOptions
        {
            Enabled = true,
            QuoteNumber = 990001,
            SourceId = "development-demo-quote-folder",
            Estimator = "ProjectTrackerAdmin",
            FolderPath = @"S:\Estimating\Development Demo\Quote 990001"
        });
        var seeder = new DevelopmentQuoteFolderDemoSeeder(
            db,
            options,
            new FrozenClock(now),
            NullLogger<DevelopmentQuoteFolderDemoSeeder>.Instance);

        await seeder.SeedAsync();
        await seeder.SeedAsync();

        var quote = Assert.Single(await db.QuoteHistory.ToListAsync());
        Assert.Equal(990001, quote.QuoteNumber);
        Assert.Equal("[DEVELOPMENT DEMO] Quote folder link", quote.Customer);
        Assert.Equal("ProjectTrackerAdmin", quote.EstimatingRep);
        Assert.Equal(@"S:\Estimating\Development Demo\Quote 990001", quote.QuoteFolderPath);
        Assert.False(quote.IsCompleted);
        Assert.Equal(now, quote.FirstImportedAt);
        Assert.Equal(0, quote.Version);

        var workflow = new EstimatingQuoteWorkflowService(db, new FrozenClock(now));
        var visible = Assert.Single(await workflow.GetMineAsync(
            new EstimatingAccessProfile(
                1,
                "DEV\\ProjectTrackerAdmin",
                "ProjectTrackerAdmin",
                EstimatingRoles.Admin,
                true),
            default));
        Assert.Equal(990001, visible.QuoteNumber);
    }

    [Fact]
    public async Task Disabled_seed_does_not_write_demo_data()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new EstimatingAccessDbContext(
            new DbContextOptionsBuilder<EstimatingAccessDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        var seeder = new DevelopmentQuoteFolderDemoSeeder(
            db,
            Options.Create(new DevelopmentQuoteFolderDemoOptions { Enabled = false }),
            TimeProvider.System,
            NullLogger<DevelopmentQuoteFolderDemoSeeder>.Instance);

        await seeder.SeedAsync();

        Assert.Empty(await db.QuoteHistory.ToListAsync());
    }

    private sealed class FrozenClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
