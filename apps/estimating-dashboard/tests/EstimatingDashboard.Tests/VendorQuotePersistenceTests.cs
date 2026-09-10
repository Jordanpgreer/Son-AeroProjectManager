using EstimatingDashboard.Api.Auth;
using EstimatingDashboard.Api.Endpoints;
using EstimatingDashboard.Api.Models;
using EstimatingDashboard.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static EstimatingDashboard.Tests.VendorQuoteTestFixture;

namespace EstimatingDashboard.Tests;

public sealed class VendorQuotePersistenceTests
{
    [Fact]
    public async Task Additive_schema_initializes_existing_database_twice_and_matches_model()
    {
        await using var f = await CreateAsync();
        // These are fixed table names in a private in-memory fixture, never the development database.
        await f.Db.Database.ExecuteSqlRawAsync("""
            DROP TABLE EstimatingVendorAttachments;
            DROP TABLE EstimatingVendorMessages;
            DROP TABLE EstimatingVendorActivities;
            DROP TABLE EstimatingVendorRequests;
            DROP TABLE EstimatingVendorSyncStates;
            DROP TABLE EstimatingQuoteStatusActivities;
            DROP TABLE EstimatingQuoteStatusMetadata;
            """);
        var initializer = new VendorQuoteSchemaInitializer(f.Db);
        await initializer.InitializeAsync();
        await initializer.InitializeAsync();
        var thread = await f.ThreadAsync("PART-1");
        await f.Vendors.ImportAsync(Message() with { Attachments = [new("rates.pdf", null, "dGVzdA==")] }, Editor, default);
        await f.Quotes.UpdateAsync(f.QuoteId(), new(0, "In progress", Now.Date.AddDays(1), "Started"), Editor, default);
        await f.Vendors.HeartbeatAsync(new("casey@company.example", "laptop", 1, 0, 0), Editor, default);
        Assert.Equal(3, await f.Db.QuoteHistory.CountAsync());
        Assert.Single((await f.Vendors.DetailAsync(thread.Request.Id, Editor, default)).Messages.Single().Attachments);
        Assert.Single(await f.Db.Set<QuoteStatusMetadata>().ToListAsync());
        Assert.Single(await f.Db.Set<VendorQuoteSyncState>().ToListAsync());
    }

    [Fact]
    public async Task Stale_tracked_entity_conflicts_at_database_boundary_without_partial_note()
    {
        await using var f = await CreateAsync();
        var first = await f.ThreadAsync();
        await using var otherDb = f.AnotherContext();
        var other = new VendorQuoteService(otherDb, TimeProvider.System);
        await other.AddNoteAsync(first.Request.Id, new(0, "Other editor"), Editor, default);
        // f retains version zero in its change tracker, so only the EF concurrency condition can catch this.
        var error = await Assert.ThrowsAsync<VendorQuoteException>(() => f.Vendors.AddNoteAsync(first.Request.Id, new(0, "Stale editor"), Editor, default));
        Assert.Equal(409, error.StatusCode);
        Assert.Equal(1, await otherDb.Set<VendorQuoteActivity>().CountAsync(x => x.Kind == "note"));
    }

    [Fact]
    public async Task Unique_dedup_constraint_prevents_duplicate_evidence_even_outside_service()
    {
        await using var f = await CreateAsync();
        await f.Vendors.ImportAsync(Message(), Editor, default);
        var existing = await f.Db.Set<VendorQuoteMessage>().SingleAsync();
        f.Db.Add(new VendorQuoteMessage { QuoteHistoryId = existing.QuoteHistoryId, RequestId = existing.RequestId,
            DeduplicationKey = existing.DeduplicationKey, SentAt = Now, ImportedAt = Now });
        await Assert.ThrowsAsync<DbUpdateException>(() => f.Db.SaveChangesAsync());
        Assert.Equal(1, await f.Db.Set<VendorQuoteMessage>().AsNoTracking().CountAsync());
    }

    [Fact]
    public void Sql_server_schema_is_additive_and_preserves_correspondence_on_parent_deletion()
    {
        var sql = VendorQuoteSchemaInitializer.CreateSql(false);
        Assert.Contains("IF OBJECT_ID", sql);
        Assert.Contains("varbinary(max)", sql);
        Assert.Contains("ON DELETE NO ACTION", sql);
        Assert.DoesNotContain("DROP ", sql);
        Assert.DoesNotContain("DELETE FROM", sql);
    }

    [Fact]
    public void Every_route_requires_history_and_every_write_requires_editor()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddAuthorization();
        builder.Services.AddScoped<VendorQuoteService>();
        builder.Services.AddScoped<QuoteStatusService>();
        var app = builder.Build();
        app.MapGroup("/api").MapVendorQuoteEndpoints().MapQuoteStatusEndpoints();
        var routes = ((IEndpointRouteBuilder)app).DataSources.SelectMany(x => x.Endpoints).OfType<RouteEndpoint>().ToList();
        Assert.Equal(16, routes.Count);
        Assert.All(routes, route =>
        {
            var auth = route.Metadata.GetOrderedMetadata<IAuthorizeData>();
            Assert.Contains(auth, x => x.Policy == EstimatingPolicies.ViewHistory);
            if (route.Metadata.GetMetadata<IHttpMethodMetadata>()!.HttpMethods.Any(x => x is "POST" or "PUT"))
                Assert.Contains(auth, x => x.Policy == EstimatingPolicies.Editor);
        });
    }
}
