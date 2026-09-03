using EstimatingDashboard.Api.Auth;
using EstimatingDashboard.Api.Data;
using EstimatingDashboard.Api.Dtos;
using EstimatingDashboard.Api.Models;
using EstimatingDashboard.Api.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace EstimatingDashboard.Tests;

public sealed class EstimatingQuoteWorkflowServiceTests
{
    [Fact]
    public async Task Mine_returns_only_active_quotes_assigned_to_the_current_estimator()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Db.QuoteHistory.AddRange(
            Quote(1001, "Casey Lee"),
            Quote(1002, "Someone Else"),
            Quote(1003, "Casey Lee", completed: true));
        await fixture.Db.SaveChangesAsync();

        var result = await fixture.Service.GetMineAsync(Editor("Casey Lee"), default);

        var quote = Assert.Single(result);
        Assert.Equal(1001, quote.QuoteNumber);
        Assert.Equal("Needs Approval", quote.FulcrumQuoteStatus);
    }

    [Theory]
    [InlineData("2026-09-07", "2026-09-03")]
    [InlineData("2026-09-08", "2026-09-07")]
    [InlineData("2026-09-09", "2026-09-08")]
    [InlineData("2026-09-10", "2026-09-09")]
    [InlineData("2026-09-11", "2026-09-10")]
    [InlineData("2026-09-12", "2026-09-10")]
    [InlineData("2026-09-13", "2026-09-10")]
    public void Automatic_estimating_due_date_uses_the_previous_Monday_through_Thursday_workday(
        string rfqDueDate,
        string expected)
    {
        Assert.Equal(
            DateTime.Parse(expected),
            EstimatingDueDates.AutomaticFromRfq(DateTime.Parse(rfqDueDate)));
    }

    [Fact]
    public async Task Personal_quote_uses_automatic_estimating_due_date_until_overridden()
    {
        await using var fixture = await Fixture.CreateAsync();
        var record = Quote(1004, "Casey Lee");
        record.RfqDueDate = new DateTime(2026, 9, 14);
        fixture.Db.QuoteHistory.Add(record);
        await fixture.Db.SaveChangesAsync();

        var automatic = Assert.Single(await fixture.Service.GetMineAsync(Editor("Casey Lee"), default));
        Assert.Equal(new DateTime(2026, 9, 10), automatic.AutomaticEstimatingDueDate);
        Assert.Equal(new DateTime(2026, 9, 10), automatic.EstimatingDueDate);
        Assert.False(automatic.EstimatingDueDateIsOverride);

        var overridden = await fixture.Service.UpdateAsync(
            record.Id,
            new UpdateEstimatingQuoteWorkflowDto(
                null,
                null,
                new DateTime(2026, 9, 9),
                0),
            Editor("Casey Lee"),
            default);
        Assert.Equal(new DateTime(2026, 9, 10), overridden.AutomaticEstimatingDueDate);
        Assert.Equal(new DateTime(2026, 9, 9), overridden.EstimatingDueDate);
        Assert.True(overridden.EstimatingDueDateIsOverride);

        var restoredAutomatic = await fixture.Service.UpdateAsync(
            record.Id,
            new UpdateEstimatingQuoteWorkflowDto(null, null, null, 1),
            Editor("Casey Lee"),
            default);
        Assert.Equal(new DateTime(2026, 9, 10), restoredAutomatic.EstimatingDueDate);
        Assert.False(restoredAutomatic.EstimatingDueDateIsOverride);
    }

    [Fact]
    public async Task Estimator_can_update_owned_Arda_workflow_without_changing_Fulcrum_fields()
    {
        var now = new DateTimeOffset(2026, 9, 3, 18, 30, 0, TimeSpan.Zero);
        await using var fixture = await Fixture.CreateAsync(now);
        var record = Quote(2001, "Casey Lee");
        record.EstimatingStatus = "Fulcrum review";
        record.Version = 4;
        fixture.Db.QuoteHistory.Add(record);
        await fixture.Db.SaveChangesAsync();

        var updated = await fixture.Service.UpdateAsync(
            record.Id,
            new UpdateEstimatingQuoteWorkflowDto(
                EstimatingArdaStatuses.InProgress,
                "Waiting for material pricing.",
                new DateTime(2026, 9, 8),
                4),
            Editor("Casey Lee"),
            default);

        Assert.Equal(EstimatingArdaStatuses.InProgress, updated.ArdaStatus);
        Assert.Equal("Waiting for material pricing.", updated.ArdaStatusNotes);
        Assert.Equal(new DateTime(2026, 9, 8), updated.EstimatingDueDate);
        Assert.True(updated.EstimatingDueDateIsOverride);
        Assert.Equal(now, updated.ArdaStatusChangedAt);
        Assert.Equal("SONAERO\\casey", updated.ArdaStatusChangedBy);
        Assert.Equal(5, updated.Version);
        Assert.Equal("Needs Approval", updated.FulcrumQuoteStatus);
        Assert.Equal("Casey Lee", updated.EstimatingRep);
        Assert.Equal(3, await fixture.Db.QuoteHistoryAudits.CountAsync());
        Assert.All(
            await fixture.Db.QuoteHistoryAudits.ToListAsync(),
            audit => Assert.Equal(EstimatingQuoteAuditActions.WorkflowUpdated, audit.Action));
    }

    [Fact]
    public async Task Notes_only_update_keeps_the_status_changed_timestamp()
    {
        var originallyChanged = new DateTimeOffset(2026, 8, 30, 14, 0, 0, TimeSpan.Zero);
        await using var fixture = await Fixture.CreateAsync(
            new DateTimeOffset(2026, 9, 3, 18, 30, 0, TimeSpan.Zero));
        var record = Quote(2002, "Casey Lee");
        record.ArdaStatus = EstimatingArdaStatuses.InProgress;
        record.ArdaStatusChangedAt = originallyChanged;
        record.ArdaStatusChangedBy = "SONAERO\\casey";
        fixture.Db.QuoteHistory.Add(record);
        await fixture.Db.SaveChangesAsync();

        var updated = await fixture.Service.UpdateAsync(
            record.Id,
            new UpdateEstimatingQuoteWorkflowDto(
                EstimatingArdaStatuses.InProgress,
                "New note",
                null,
                0),
            Editor("Casey Lee"),
            default);

        Assert.Equal(originallyChanged, updated.ArdaStatusChangedAt);
        Assert.Equal("SONAERO\\casey", updated.ArdaStatusChangedBy);
        Assert.Equal(1, updated.Version);
    }

    [Fact]
    public async Task Editor_cannot_update_another_estimators_quote()
    {
        await using var fixture = await Fixture.CreateAsync();
        var record = Quote(3001, "Another Estimator");
        fixture.Db.QuoteHistory.Add(record);
        await fixture.Db.SaveChangesAsync();

        await Assert.ThrowsAsync<EstimatingQuoteWorkflowForbiddenException>(() =>
            fixture.Service.UpdateAsync(
                record.Id,
                new UpdateEstimatingQuoteWorkflowDto(
                    EstimatingArdaStatuses.OnHold,
                    null,
                    null,
                    0),
                Editor("Casey Lee"),
                default));

        Assert.Null(record.ArdaStatus);
    }

    [Fact]
    public async Task First_name_only_assignment_is_rejected_when_that_name_is_ambiguous()
    {
        await using var fixture = await Fixture.CreateAsync();
        var ambiguous = Quote(3003, "Casey");
        fixture.Db.QuoteHistory.AddRange(ambiguous, Quote(3004, "Casey Lee"));
        await fixture.Db.SaveChangesAsync();
        var access = new EstimatingAccessProfile(
            2,
            "SONAERO\\cjones",
            "Casey Jones",
            EstimatingRoles.Editor,
            true);

        Assert.Empty(await fixture.Service.GetMineAsync(access, default));
        await Assert.ThrowsAsync<EstimatingQuoteWorkflowForbiddenException>(() =>
            fixture.Service.UpdateAsync(
                ambiguous.Id,
                new UpdateEstimatingQuoteWorkflowDto(
                    EstimatingArdaStatuses.InProgress,
                    null,
                    null,
                    0),
                access,
                default));
    }

    [Fact]
    public async Task Stale_version_is_rejected_before_any_change()
    {
        await using var fixture = await Fixture.CreateAsync();
        var record = Quote(3002, "Casey Lee");
        record.Version = 6;
        fixture.Db.QuoteHistory.Add(record);
        await fixture.Db.SaveChangesAsync();

        await Assert.ThrowsAsync<EstimatingQuoteWorkflowConflictException>(() =>
            fixture.Service.UpdateAsync(
                record.Id,
                new UpdateEstimatingQuoteWorkflowDto(
                    EstimatingArdaStatuses.Complete,
                    null,
                    null,
                    5),
                Editor("Casey Lee"),
                default));

        Assert.Null(record.ArdaStatus);
    }

    [Fact]
    public async Task Schema_initializer_adds_Arda_workflow_columns_to_an_existing_Sqlite_history_table()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                CREATE TABLE "EstimatingQuoteHistory" (
                    "Id" INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                    "SourceId" TEXT NOT NULL,
                    "QuoteNumber" INTEGER NOT NULL,
                    "Customer" TEXT NOT NULL,
                    "SalesPerson" TEXT NOT NULL,
                    "QuoteStatus" TEXT NOT NULL,
                    "EstimatingRep" TEXT NOT NULL,
                    "TotalValue" TEXT NOT NULL,
                    "RfqDueDate" TEXT NULL,
                    "ArdaDueDate" TEXT NULL,
                    "DateToEstimating" TEXT NULL,
                    "Issues" TEXT NULL,
                    "QuoteComplexity" TEXT NULL,
                    "NumberOfParts" INTEGER NOT NULL,
                    "EstimatingStatus" TEXT NULL,
                    "EstimatingCompletionDate" TEXT NULL,
                    "OnTimeStatus" TEXT NOT NULL,
                    "DaysLate" INTEGER NOT NULL,
                    "Workdays" INTEGER NULL,
                    "CompletedMonth" TEXT NULL,
                    "CompletedYear" INTEGER NULL,
                    "CompletedWeekOfMonth" INTEGER NULL,
                    "CompletedMonthAndWeek" TEXT NULL,
                    "IsCompleted" INTEGER NOT NULL,
                    "CompletedWeekOfYear" INTEGER NULL,
                    "IsOnTime" INTEGER NOT NULL,
                    "OnTimeRatio" TEXT NULL,
                    "LastImportBatchId" TEXT NOT NULL,
                    "FirstImportedAt" TEXT NOT NULL,
                    "UpdatedAt" TEXT NOT NULL,
                    "UpdatedBy" TEXT NOT NULL,
                    "Version" INTEGER NOT NULL DEFAULT 0
                );
                INSERT INTO "EstimatingQuoteHistory" (
                    "SourceId", "QuoteNumber", "Customer", "SalesPerson", "QuoteStatus",
                    "EstimatingRep", "TotalValue", "NumberOfParts", "OnTimeStatus",
                    "DaysLate", "IsCompleted", "IsOnTime", "LastImportBatchId",
                    "FirstImportedAt", "UpdatedAt", "UpdatedBy", "Version", "ArdaDueDate"
                ) VALUES (
                    'legacy-quote', 9001, 'Legacy customer', 'Sales', 'Needs Approval',
                    'Casey Lee', '100.00', 1, 'NoData', 0, 0, 0,
                    '00000000-0000-0000-0000-000000000000',
                    '2026-09-01T00:00:00Z', '2026-09-01T00:00:00Z', 'Test', 0,
                    '2026-09-09'
                );
                """;
            await command.ExecuteNonQueryAsync();
        }

        var options = new DbContextOptionsBuilder<EstimatingAccessDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new EstimatingAccessDbContext(options);
        await new EstimatingHistorySchemaInitializer(db).InitializeAsync();

        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var inspect = connection.CreateCommand();
        inspect.CommandText = "PRAGMA table_info(\"EstimatingQuoteHistory\")";
        await using var reader = await inspect.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            columns.Add(reader.GetString(reader.GetOrdinal("name")));

        Assert.Contains("ArdaStatus", columns);
        Assert.Contains("ArdaStatusNotes", columns);
        Assert.Contains("ArdaStatusChangedAt", columns);
        Assert.Contains("ArdaStatusChangedBy", columns);
        Assert.Contains("EstimatingDueDateOverride", columns);

        await reader.DisposeAsync();
        await using var migratedValue = connection.CreateCommand();
        migratedValue.CommandText = "SELECT \"EstimatingDueDateOverride\" FROM \"EstimatingQuoteHistory\" WHERE \"QuoteNumber\" = 9001";
        Assert.Equal("2026-09-09", Convert.ToString(await migratedValue.ExecuteScalarAsync()));
    }

    private static EstimatingQuoteHistoryRecord Quote(
        int quoteNumber,
        string estimator,
        bool completed = false) => new()
    {
        SourceId = $"fulcrum-{quoteNumber}",
        QuoteNumber = quoteNumber,
        Customer = $"Customer {quoteNumber}",
        SalesPerson = "Sales",
        QuoteStatus = completed ? "Quoted" : "Needs Approval",
        EstimatingRep = estimator,
        TotalValue = 1250m,
        IsCompleted = completed,
        OnTimeStatus = EstimatingOnTimeStatuses.NoData,
        LastImportBatchId = Guid.NewGuid(),
        FirstImportedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
        UpdatedBy = "Test"
    };

    private static EstimatingAccessProfile Editor(string displayName) => new(
        1,
        "SONAERO\\casey",
        displayName,
        EstimatingRoles.Editor,
        true);

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly SqliteConnection connection;

        private Fixture(
            SqliteConnection connection,
            EstimatingAccessDbContext db,
            EstimatingQuoteWorkflowService service)
        {
            this.connection = connection;
            Db = db;
            Service = service;
        }

        public EstimatingAccessDbContext Db { get; }
        public EstimatingQuoteWorkflowService Service { get; }

        public static async Task<Fixture> CreateAsync(DateTimeOffset? now = null)
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<EstimatingAccessDbContext>()
                .UseSqlite(connection)
                .Options;
            var db = new EstimatingAccessDbContext(options);
            await db.Database.EnsureCreatedAsync();
            var clock = new FixedTimeProvider(now ?? DateTimeOffset.UtcNow);
            return new Fixture(
                connection,
                db,
                new EstimatingQuoteWorkflowService(db, clock));
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await connection.DisposeAsync();
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
