using EstimatingDashboard.Api.Auth;
using EstimatingDashboard.Api.Data;
using EstimatingDashboard.Api.Dtos;
using EstimatingDashboard.Api.Models;
using EstimatingDashboard.Api.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace EstimatingDashboard.Tests;

internal sealed class VendorQuoteTestFixture : IAsyncDisposable
{
    private readonly SqliteConnection connection;
    public EstimatingAccessDbContext Db { get; }
    public VendorQuoteService Vendors { get; }
    public QuoteStatusService Quotes { get; }
    public EstimatingQuoteWorkflowService Workflow { get; }
    public static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-10T16:00:00Z");
    public static EstimatingAccessProfile Editor => new(1, "SONAERO\\casey", "Casey Lee", EstimatingRoles.Editor, true);
    public static EstimatingAccessProfile Admin => new(2, "SONAERO\\admin", "Admin User", EstimatingRoles.Admin, true);
    private VendorQuoteTestFixture(SqliteConnection conn, EstimatingAccessDbContext db)
    {
        connection = conn; Db = db;
        var clock = new FrozenClock();
        Vendors = new(db, clock); Workflow = new(db, clock); Quotes = new(db, clock, Vendors, Workflow);
    }
    public static async Task<VendorQuoteTestFixture> CreateAsync()
    {
        var conn = new SqliteConnection("Data Source=:memory:");
        await conn.OpenAsync();
        var db = new EstimatingAccessDbContext(new DbContextOptionsBuilder<EstimatingAccessDbContext>().UseSqlite(conn).Options);
        await db.Database.EnsureCreatedAsync();
        db.QuoteHistory.AddRange(Record(4445, "Casey Lee"), Record(44450, "Someone Else"), Record(4451, "Casey Lee", true));
        await db.SaveChangesAsync();
        return new(conn, db);
    }
    public static EstimatingQuoteHistoryRecord Record(int number, string estimator, bool complete = false) => new()
    {
        QuoteNumber = number, SourceId = "test-" + number, Customer = "Synthetic customer", EstimatingRep = estimator,
        FirstImportedAt = Now.AddDays(-2), UpdatedAt = Now.AddDays(-1), UpdatedBy = "test", IsCompleted = complete
    };
    public int QuoteId(int number = 4445) => Db.QuoteHistory.Single(x => x.QuoteNumber == number).Id;
    public EstimatingAccessDbContext AnotherContext() => new(new DbContextOptionsBuilder<EstimatingAccessDbContext>().UseSqlite(connection).Options);
    public Task<VendorQuoteDetailDto> ThreadAsync(string? part = null, string email = "quotes@vendor.example", string status = "Untouched") =>
        Vendors.CreateAsync(new(QuoteId(), "Silicone Prime", email, "Material pricing", status, null, null, part), Editor, default);
    public static ImportVendorQuoteMessageDto Message(string id = "<message-1@vendor.example>", string subject = "RE: Quote 4445") =>
        new(id, "casey@company.example", "incoming", subject, "quotes@vendor.example", "Silicone Prime",
            ["casey@company.example"], "quotes@vendor.example", "Silicone Prime", Now, Now, "Pricing attached.", [], "conversation-1");
    public async ValueTask DisposeAsync() { await Db.DisposeAsync(); await connection.DisposeAsync(); }
    private sealed class FrozenClock : TimeProvider { public override DateTimeOffset GetUtcNow() => Now; }
}
