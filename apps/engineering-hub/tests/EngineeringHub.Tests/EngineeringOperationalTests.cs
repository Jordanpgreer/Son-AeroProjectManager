using EngineeringHub.Api.Data;
using EngineeringHub.Api.Models;
using EngineeringHub.Api.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace EngineeringHub.Tests;

public sealed class EngineeringOperationalTests
{
    [Fact]
    public async Task DemoSeedIsIdempotentAndNeverCreatesFakeApprovedFiles()
    {
        await using var fixture = await ContextFixture.CreateAsync();
        var seeder = new EngineeringDemoDataSeeder(fixture.Context);

        await seeder.SeedAsync(CancellationToken.None);
        await seeder.SeedAsync(CancellationToken.None);

        var drawings = await fixture.Context.Drawings.Include(x => x.Revisions).ToListAsync();
        Assert.Equal(5, drawings.Count);
        Assert.Equal(4, drawings.Sum(x => x.Revisions.Count));
        Assert.DoesNotContain(drawings.SelectMany(x => x.Revisions), x => x.Status == DrawingRevisionStatus.Approved);
        Assert.All(drawings.SelectMany(x => x.Revisions), x =>
        {
            Assert.Equal(0, x.FileSize);
            Assert.Empty(x.StoredFilePath);
        });
    }

    [Fact]
    public async Task DashboardUsesLiveDrawingsAndBuildsReviewQueue()
    {
        await using var fixture = await ContextFixture.CreateAsync();
        await new EngineeringDemoDataSeeder(fixture.Context).SeedAsync(CancellationToken.None);

        var dashboard = await new EngineeringSearchService(fixture.Context)
            .GetDashboardAsync("DRW-100014-A", null, null, null, false, CancellationToken.None);

        Assert.Equal(5, dashboard.Summary.TotalDrawings);
        Assert.True(dashboard.Summary.ReviewQueue > 0);
        Assert.Contains(dashboard.Results, x => x.Category == "drawings" && x.Identifier == "DRW-100014-A");
        Assert.Contains(dashboard.Results, x =>
            x.Identifier == "DRW-100014-A" &&
            x.AttentionReasons is { Count: > 0 });

        var reviewQueue = await new EngineeringSearchService(fixture.Context)
            .GetDashboardAsync(null, null, null, null, true, CancellationToken.None);

        Assert.Equal(reviewQueue.Summary.ReviewQueue, reviewQueue.Results.Count);
        Assert.All(reviewQueue.Results, result =>
        {
            Assert.Equal("drawings", result.Category);
            Assert.NotEmpty(result.AttentionReasons ?? []);
        });
    }

    [Fact]
    public async Task DashboardPresentsStoredObsoleteDrawingAsArchived()
    {
        await using var fixture = await ContextFixture.CreateAsync();
        fixture.Context.Drawings.Add(new Drawing
        {
            DrawingNumber = "DRW-ARCHIVE-1",
            NormalizedDrawingNumber = "DRWARCHIVE1",
            Title = "Retained historical drawing",
            Customer = "ACME",
            NormalizedCustomer = "ACME",
            ApprovalStatus = DrawingApprovalStatus.Obsolete,
            IsObsolete = true,
            CreatedBy = "test-user",
            CreatedAt = DateTime.UtcNow
        });
        await fixture.Context.SaveChangesAsync();

        var dashboard = await new EngineeringSearchService(fixture.Context)
            .GetDashboardAsync("DRW-ARCHIVE-1", null, null, "Obsolete", false, CancellationToken.None);

        var result = Assert.Single(dashboard.Results);
        Assert.Contains("Archived controlled drawing", result.Subtitle);
        Assert.Contains("Obsolete", result.Tags);
    }

    private sealed class ContextFixture(SqliteConnection connection, EngineeringDbContext context) : IAsyncDisposable
    {
        public EngineeringDbContext Context { get; } = context;

        public static async Task<ContextFixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var context = new EngineeringDbContext(
                new DbContextOptionsBuilder<EngineeringDbContext>().UseSqlite(connection).Options);
            await context.Database.EnsureCreatedAsync();
            return new ContextFixture(connection, context);
        }

        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
            await connection.DisposeAsync();
        }
    }
}
