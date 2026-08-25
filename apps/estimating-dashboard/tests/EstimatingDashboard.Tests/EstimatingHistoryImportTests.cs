using ClosedXML.Excel;
using EstimatingDashboard.Api.Auth;
using EstimatingDashboard.Api.Data;
using EstimatingDashboard.Api.Models;
using EstimatingDashboard.Api.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace EstimatingDashboard.Tests;

public sealed class EstimatingHistoryImportTests
{
    private static readonly string[] Headers =
    [
        "Id",
        "Number",
        "Customer",
        "SalesPerson",
        "TotalInPrimaryCurrency",
        "Status",
        "RFQ Due Date",
        "Date to Estimating",
        "Issues?",
        "Quote Complexity",
        "Number of Parts in Quote",
        "Estimating Status",
        "Estimating Rep",
        "Estimating Completion Date"
    ];

    [Fact]
    public async Task ImportTransformsFulcrumRowsAndBuildsEstimatorStatistics()
    {
        await using var fixture = await Fixture.CreateAsync();
        await using var workbook = Workbook([
            ["source-1", 4395, "Acme Aerospace", "Sales One", 1250m, "Sent", new DateTime(2026, 8, 27), new DateTime(2026, 8, 24), "None", "B (Medium)", 2, "Assembled Estimate", "Bethany", new DateTime(2026, 8, 27)],
            ["source-2", 4396, "Acme Aerospace", "Sales Two", 2500m, "Needs Approval", new DateTime(2026, 9, 1), new DateTime(2026, 8, 24), "Missing Information", "A (High)", 4, "Quote Assigned", "Bethany", null]
        ]);

        var validation = await fixture.Importer.ValidateAsync(workbook, "Grid Results.xlsx", "TEST\\admin", default);
        Assert.Equal(2, validation.NewRecords);
        Assert.Equal(0, validation.ErrorRows);

        var applied = await fixture.Importer.ApplyAsync(validation.ReviewId, "TEST\\admin", false, default);
        Assert.Equal(2, applied.NewRecords);
        var records = await fixture.Db.QuoteHistory.OrderBy(record => record.QuoteNumber).ToListAsync();
        Assert.Equal(2, records.Count);
        var creationAudits = await fixture.Db.QuoteHistoryAudits
            .Where(audit => audit.Action == EstimatingQuoteAuditActions.Created)
            .ToListAsync();
        Assert.Equal(2, creationAudits.Count);
        Assert.All(creationAudits, audit => Assert.Equal("TEST\\admin", audit.ChangedBy));
        Assert.Equal(4, records[0].Workdays);
        Assert.Equal(EstimatingOnTimeStatuses.OnTime, records[0].OnTimeStatus);
        Assert.Equal(1m, records[0].OnTimeRatio);
        Assert.True(records[0].IsCompleted);
        Assert.False(records[1].IsCompleted);

        var dashboard = await fixture.Queries.GetDashboardAsync("all", ManagerAccess(), default);
        var bethany = Assert.Single(dashboard.Users);
        Assert.Equal(1, bethany.InQueue);
        Assert.Equal(1, bethany.CompletedAllTime);
        Assert.Equal(3750m, bethany.TotalQuoteValue);
        Assert.Equal(4d, bethany.AverageCompletionWorkdays);
    }

    [Fact]
    public async Task ReuploadDetectsUnchangedAndUpdatesIncludedRowsWithoutDeletingOmissions()
    {
        await using var fixture = await Fixture.CreateAsync();
        await using var initial = Workbook([
            ["source-1", 1001, "Customer One", "Sales", 100m, "Draft", null, new DateTime(2026, 8, 20), "None", "C (Low)", 1, "Quote Assigned", "Darlene", null],
            ["source-2", 1002, "Customer Two", "Sales", 200m, "Sent", new DateTime(2026, 8, 25), new DateTime(2026, 8, 21), "None", "B (Medium)", 1, "Assembled Estimate", "Darlene", new DateTime(2026, 8, 24)]
        ]);
        var first = await fixture.Importer.ValidateAsync(initial, "first.xlsx", "TEST\\admin", default);
        await fixture.Importer.ApplyAsync(first.ReviewId, "TEST\\admin", false, default);

        await using var update = Workbook([
            ["replacement-source", 1001, "Customer One", "Sales", 950m, "Sent", null, new DateTime(2026, 8, 20), "None", "C (Low)", 1, "Reviewed Quote", "Darlene", null]
        ]);
        var validation = await fixture.Importer.ValidateAsync(update, "update.xlsx", "TEST\\admin", default);
        Assert.Equal(1, validation.UpdatedRecords);
        Assert.Equal(0, validation.NewRecords);
        await fixture.Importer.ApplyAsync(validation.ReviewId, "TEST\\admin", false, default);

        var records = await fixture.Db.QuoteHistory.OrderBy(record => record.QuoteNumber).ToListAsync();
        Assert.Equal(2, records.Count);
        Assert.Equal("replacement-source", records[0].SourceId);
        Assert.Equal(950m, records[0].TotalValue);
        Assert.Equal("Reviewed Quote", records[0].EstimatingStatus);
        Assert.Equal(200m, records[1].TotalValue);

        var updateAudits = await fixture.Db.QuoteHistoryAudits
            .Where(audit => audit.QuoteHistoryId == records[0].Id
                && audit.Action == EstimatingQuoteAuditActions.Updated)
            .ToListAsync();
        Assert.Contains(updateAudits, audit =>
            audit.FieldName == "Fulcrum source ID"
            && audit.OldValue == "source-1"
            && audit.NewValue == "replacement-source");
        Assert.Contains(updateAudits, audit =>
            audit.FieldName == "Total quote value"
            && audit.OldValue == "100.00"
            && audit.NewValue == "950.00");
        Assert.Contains(updateAudits, audit =>
            audit.FieldName == "Estimating status"
            && audit.OldValue == "Quote Assigned"
            && audit.NewValue == "Reviewed Quote");
        Assert.All(updateAudits, audit => Assert.Equal("TEST\\admin", audit.ChangedBy));

        var auditHistory = await fixture.Queries.GetAuditHistoryAsync(records[0].Id, default);
        Assert.NotNull(auditHistory);
        Assert.Equal(1001, auditHistory.QuoteNumber);
        Assert.Equal(2, auditHistory.Events.Count);
        Assert.Equal(EstimatingQuoteAuditActions.Updated, auditHistory.Events[0].Action);
        Assert.Equal(EstimatingQuoteAuditActions.Created, auditHistory.Events[1].Action);

        await using var unchanged = Workbook([
            ["replacement-source", 1001, "Customer One", "Sales", 950m, "Sent", null, new DateTime(2026, 8, 20), "None", "C (Low)", 1, "Reviewed Quote", "Darlene", null]
        ]);
        var unchangedValidation = await fixture.Importer.ValidateAsync(unchanged, "unchanged.xlsx", "TEST\\admin", default);
        Assert.Equal(1, unchangedValidation.UnchangedRecords);
        Assert.False(unchangedValidation.CanApply);
    }

    [Fact]
    public async Task ValidationRejectsMissingColumnsAndDuplicateSourceIds()
    {
        await using var fixture = await Fixture.CreateAsync();
        await using var missingHeader = Workbook([], Headers.Where(header => header != "Customer").ToArray());
        var missing = await fixture.Importer.ValidateAsync(missingHeader, "missing.xlsx", "TEST\\admin", default);
        Assert.Contains(missing.Errors, error => error.Column == "Customer");

        await using var duplicates = Workbook([
            ["duplicate", 1001, "One", "Sales", 100m, "Draft", null, null, "None", "C (Low)", 1, null, "Abel", null],
            ["duplicate", 1002, "Two", "Sales", 200m, "Draft", null, null, "None", "C (Low)", 1, null, "Abel", null]
        ]);
        var duplicateResult = await fixture.Importer.ValidateAsync(duplicates, "duplicate.xlsx", "TEST\\admin", default);
        Assert.Equal(2, duplicateResult.ErrorRows);
        Assert.Equal(0, duplicateResult.NewRecords);
    }

    [Fact]
    public async Task ValidationRejectsDuplicateQuoteNumbersWithinOneWorkbook()
    {
        await using var fixture = await Fixture.CreateAsync();
        await using var duplicates = Workbook([
            ["source-1", 1001, "One", "Sales", 100m, "Draft", null, null, "None", "C (Low)", 1, null, "Abel", null],
            ["source-2", 1001, "Two", "Sales", 200m, "Draft", null, null, "None", "C (Low)", 1, null, "Abel", null]
        ]);

        var result = await fixture.Importer.ValidateAsync(duplicates, "duplicate-number.xlsx", "TEST\\admin", default);

        Assert.Equal(2, result.ErrorRows);
        Assert.Equal(0, result.NewRecords);
        Assert.Contains(result.Errors, error =>
            error.Column == "Number"
            && error.Message.Contains("more than once", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task QueryFiltersQueueAndValueRange()
    {
        await using var fixture = await Fixture.CreateAsync();
        await using var workbook = Workbook([
            ["source-1", 1001, "Queue Customer", "Sales", 500m, "Draft", null, new DateTime(2026, 8, 20), "None", "C (Low)", 1, "Quote Assigned", "Abel", null],
            ["source-2", 1002, "Complete Customer", "Sales", 1500m, "Sent", new DateTime(2026, 8, 25), new DateTime(2026, 8, 20), "None", "B (Medium)", 1, "Assembled Estimate", "Abel", new DateTime(2026, 8, 24)]
        ]);
        var validation = await fixture.Importer.ValidateAsync(workbook, "filter.xlsx", "TEST\\admin", default);
        await fixture.Importer.ApplyAsync(validation.ReviewId, "TEST\\admin", false, default);

        var page = await fixture.Queries.GetPageAsync(
            null, "Abel", null, null, null, null, null, null, null, null, "queue", null,
            null, null, null, null, 400m, 600m, "value", "desc", 1, 50, default);
        var record = Assert.Single(page.Records);
        Assert.Equal(1001, record.QuoteNumber);
    }

    [Fact]
    public async Task ApplyRequiresConfirmationWhenErrorsExistAndSkipsOnlyInvalidRows()
    {
        await using var fixture = await Fixture.CreateAsync();
        await using var workbook = Workbook([
            ["valid-source", 1001, "Valid Customer", "Sales", 500m, "Draft", null, new DateTime(2026, 8, 20), "None", "C (Low)", 1, "Quote Assigned", "Abel", null],
            ["", 1002, "Invalid Customer", "Sales", 700m, "Draft", null, new DateTime(2026, 8, 21), "None", "C (Low)", 1, "Quote Assigned", "Abel", null]
        ]);

        var validation = await fixture.Importer.ValidateAsync(workbook, "errors.xlsx", "TEST\\admin", default);
        Assert.Equal(1, validation.NewRecords);
        Assert.Equal(1, validation.ErrorRows);
        await Assert.ThrowsAsync<EstimatingHistoryImportValidationException>(() =>
            fixture.Importer.ApplyAsync(validation.ReviewId, "TEST\\admin", false, default));

        var result = await fixture.Importer.ApplyAsync(validation.ReviewId, "TEST\\admin", true, default);
        Assert.Equal(1, result.NewRecords);
        Assert.Equal(1, result.SkippedRows);
        var saved = Assert.Single(await fixture.Db.QuoteHistory.ToListAsync());
        Assert.Equal("valid-source", saved.SourceId);
    }

    [Fact]
    public async Task LegacyDailyQuoteLogJoinsTablesAndBuildsNeedsApprovalQueue()
    {
        await using var fixture = await Fixture.CreateAsync();
        await using var workbook = LegacyWorkbook();

        var validation = await fixture.Importer.ValidateAsync(workbook, "Daily Quote Log.xlsx", "TEST\\admin", default);
        Assert.Equal(2, validation.NewRecords);
        Assert.Equal(0, validation.ErrorRows);
        await fixture.Importer.ApplyAsync(validation.ReviewId, "TEST\\admin", false, default);

        var saved = await fixture.Db.QuoteHistory.SingleAsync(record => record.SourceId == "legacy-1");
        Assert.Equal("Buyer One", saved.CustomerContact);
        Assert.Equal("RFQ-100", saved.RfqReferenceNumber);
        Assert.Equal("At Risk", saved.QuoteOnTrack);

        var queue = await fixture.Queries.GetPageAsync(
            null, null, null, null, null, null, null, null, null, "live", null, null,
            null, null, null, null, null, null, "due", "asc", 1, 50, default);
        var queued = Assert.Single(queue.Records);
        Assert.Equal(1001, queued.QuoteNumber);
    }

    [Fact]
    public async Task DashboardScopesEstimatorsAndExcludesFormerEmployeeAbel()
    {
        await using var fixture = await Fixture.CreateAsync();
        var today = DateTime.Today;
        await using var workbook = Workbook([
            ["bethany-complete", 2001, "One", "Sales", 100m, "Sent", today, today, "None", "C (Low)", 1, "Assembled Estimate", "Bethany", today],
            ["darlene-queue", 2002, "Two", "Sales", 200m, "Needs Approval", today, today, "None", "B (Medium)", 1, "Quote Assigned", "Darlene", null],
            ["abel-complete", 2003, "Three", "Sales", 300m, "Sent", today, today, "None", "C (Low)", 1, "Assembled Estimate", "Abel", today],
            ["unassigned-queue", 2004, "Four", "Sales", 400m, "Needs Approval", today, today, "None", "C (Low)", 1, "Quote Assigned", "Unassigned", null]
        ]);
        var validation = await fixture.Importer.ValidateAsync(workbook, "stats.xlsx", "TEST\\admin", default);
        await fixture.Importer.ApplyAsync(validation.ReviewId, "TEST\\admin", false, default);

        var manager = await fixture.Queries.GetDashboardAsync("week", ManagerAccess(), default);
        Assert.True(manager.IsTeamView);
        Assert.Equal(2, manager.Users.Count);
        Assert.DoesNotContain(manager.Users, user => user.Estimator == "Abel");
        Assert.Equal(2, manager.Department.InQueue);
        Assert.Equal(1, manager.Department.CompletedInPeriod);

        var estimator = await fixture.Queries.GetDashboardAsync("week", EstimatorAccess("Bethany R."), default);
        Assert.False(estimator.IsTeamView);
        var bethany = Assert.Single(estimator.Users);
        Assert.Equal("Bethany", bethany.Estimator);
        Assert.Equal(1, bethany.CompletedInPeriod);
        Assert.Equal(0, estimator.Department.InQueue);
    }

    [Fact]
    public async Task ReportExcludesAbelByDefaultButAllowsExplicitFormerEstimatorFilter()
    {
        await using var fixture = await Fixture.CreateAsync();
        var today = DateTime.Today;
        await using var workbook = Workbook([
            ["bethany", 2101, "One", "Sales", 100m, "Sent", today, today, "None", "C (Low)", 1, "Assembled Estimate", "Bethany", today],
            ["abel", 2102, "Two", "Sales", 200m, "Sent", today, today, "None", "C (Low)", 1, "Assembled Estimate", "Abel", today]
        ]);
        var validation = await fixture.Importer.ValidateAsync(workbook, "report.xlsx", "TEST\\admin", default);
        await fixture.Importer.ApplyAsync(validation.ReviewId, "TEST\\admin", false, default);
        var reporter = new EstimatingHistoryReportService(fixture.Db);

        var teamReport = await reporter.CreateAsync("week", null, default);
        using var teamStream = new MemoryStream(teamReport.Content);
        using var teamWorkbook = new XLWorkbook(teamStream);
        var teamValues = teamWorkbook.Worksheet("Estimator Statistics").Column(1).CellsUsed().Select(cell => cell.GetString()).ToList();
        Assert.Contains("Bethany", teamValues);
        Assert.DoesNotContain("Abel", teamValues);

        var abelReport = await reporter.CreateAsync("week", "Abel", default);
        using var abelStream = new MemoryStream(abelReport.Content);
        using var abelWorkbook = new XLWorkbook(abelStream);
        var abelValues = abelWorkbook.Worksheet("Estimator Statistics").Column(1).CellsUsed().Select(cell => cell.GetString()).ToList();
        Assert.Contains("Abel", abelValues);
        Assert.DoesNotContain("Bethany", abelValues);
    }

    [Fact]
    public async Task SearchIncludesDisplayedColumnsButExcludesValueAndCompletionDate()
    {
        await using var fixture = await Fixture.CreateAsync();
        var dueDate = new DateTime(2026, 9, 14);
        await using var workbook = Workbook([
            ["searchable", 2201, "Search Customer", "Sales", 987654m, "Needs Approval", dueDate, dueDate.AddDays(-2), "Capacity Constraint", "A (High)", 17, "Reviewed Quote", "Darlene", new DateTime(2026, 9, 18)]
        ]);
        var validation = await fixture.Importer.ValidateAsync(workbook, "search.xlsx", "TEST\\admin", default);
        await fixture.Importer.ApplyAsync(validation.ReviewId, "TEST\\admin", false, default);

        async Task<int> Count(string search) => (await fixture.Queries.GetPageAsync(
            search, null, null, null, null, null, null, null, null, "history", null, null,
            null, null, null, null, null, null, "number", "desc", 1, 50, default)).Total;

        Assert.Equal(1, await Count("Needs Approval"));
        Assert.Equal(1, await Count("needs approval"));
        Assert.Equal(1, await Count("SEARCH CUSTOMER"));
        Assert.Equal(1, await Count("capacity constraint"));
        Assert.Equal(1, await Count("A (High)"));
        Assert.Equal(1, await Count("17"));
        Assert.Equal(1, await Count("2026-09-14"));
        Assert.Equal(0, await Count("987654"));
        Assert.Equal(0, await Count("2026-09-18"));
    }

    [Fact]
    public async Task FiltersIncludeDistinctSalespeople()
    {
        await using var fixture = await Fixture.CreateAsync();
        var today = DateTime.Today;
        await using var workbook = Workbook([
            ["sales-a", 2301, "Customer A", "Jordan", 100m, "Sent", today, today, "None", "C (Low)", 1, "Complete", "Bethany", today],
            ["sales-b", 2302, "Customer B", "Alex", 200m, "Sent", today, today, "None", "C (Low)", 1, "Complete", "Darlene", today],
            ["sales-c", 2303, "Customer C", "Jordan", 300m, "Sent", today, today, "None", "C (Low)", 1, "Complete", "Bethany", today]
        ]);
        var validation = await fixture.Importer.ValidateAsync(workbook, "salespeople.xlsx", "TEST\\admin", default);
        await fixture.Importer.ApplyAsync(validation.ReviewId, "TEST\\admin", false, default);

        var options = await fixture.Queries.GetFiltersAsync(default);

        Assert.Equal(["Alex", "Jordan"], options.SalesPersons);
    }

    [Fact]
    public async Task GridExportContainsOnlyCurrentSearchAndFilterResults()
    {
        await using var fixture = await Fixture.CreateAsync();
        var today = DateTime.Today;
        await using var workbook = Workbook([
            ["export-a", 2401, "Northstar Aerospace", "Jordan", 100m, "Sent", today, today, "None", "C (Low)", 1, "Complete", "Bethany", today],
            ["export-b", 2402, "Northstar Tooling", "Alex", 200m, "Sent", today, today, "None", "C (Low)", 1, "Complete", "Darlene", today],
            ["export-c", 2403, "Other Customer", "Jordan", 300m, "Sent", today, today, "None", "C (Low)", 1, "Complete", "Bethany", today]
        ]);
        var validation = await fixture.Importer.ValidateAsync(workbook, "export.xlsx", "TEST\\admin", default);
        await fixture.Importer.ApplyAsync(validation.ReviewId, "TEST\\admin", false, default);
        var exporter = new EstimatingHistoryGridExportService(fixture.Queries);

        var file = await exporter.CreateAsync(new EstimatingHistoryGridExportRequest(
            Search: "NORTHSTAR",
            Estimator: null,
            SalesPerson: "Jordan",
            Customer: null,
            QuoteStatus: null,
            EstimatingStatus: null,
            Complexity: null,
            Issues: null,
            QuoteOnTrack: null,
            View: "history",
            Completion: null,
            OnTime: null,
            DueFrom: null,
            DueTo: null,
            CompletedFrom: null,
            CompletedTo: null,
            MinimumValue: null,
            MaximumValue: null,
            Sort: "number",
            Direction: "asc"), default);

        using var stream = new MemoryStream(file.Content);
        using var exported = new XLWorkbook(stream);
        var sheet = exported.Worksheet("Grid Results");
        Assert.Equal("Quote", sheet.Cell(1, 1).GetString());
        Assert.Equal(2401, sheet.Cell(2, 1).GetValue<int>());
        Assert.Equal("Northstar Aerospace", sheet.Cell(2, 2).GetString());
        Assert.Equal("Jordan", sheet.Cell(2, 4).GetString());
        Assert.Equal(string.Empty, sheet.Cell(3, 1).GetString());
    }

    private static EstimatingAccessProfile ManagerAccess() => new(
        1,
        "TEST\\manager",
        "Estimating Manager",
        EstimatingRoles.Admin,
        true);

    private static EstimatingAccessProfile EstimatorAccess(string displayName) => new(
        2,
        $"TEST\\{displayName.Split(' ')[0]}",
        displayName,
        EstimatingRoles.Viewer,
        true);

    private static MemoryStream Workbook(IReadOnlyList<object?[]> rows, string[]? headers = null)
    {
        using var workbook = new XLWorkbook();
        var sheet = workbook.Worksheets.Add("Grid Results");
        var actualHeaders = headers ?? Headers;
        for (var column = 0; column < actualHeaders.Length; column++)
            sheet.Cell(1, column + 1).Value = actualHeaders[column];
        for (var row = 0; row < rows.Count; row++)
        {
            for (var column = 0; column < rows[row].Length && column < actualHeaders.Length; column++)
            {
                var value = rows[row][column];
                if (value is not null) sheet.Cell(row + 2, column + 1).Value = XLCellValue.FromObject(value);
            }
        }
        var stream = new MemoryStream();
        workbook.SaveAs(stream);
        stream.Position = 0;
        return stream;
    }

    private static MemoryStream LegacyWorkbook()
    {
        using var workbook = new XLWorkbook();
        var source = workbook.Worksheets.Add("Daily Quote Log Data");
        var sourceHeaders = new[]
        {
            "Id", "Number", "Customer", "CustomerContact", "SalesPerson",
            "TotalInPrimaryCurrency", "Status", "Index"
        };
        for (var column = 0; column < sourceHeaders.Length; column++)
            source.Cell(1, column + 1).Value = sourceHeaders[column];
        var sourceRows = new object?[][]
        {
            ["legacy-1", 1001, "Customer One", "Buyer One", "Sales One", 500m, "Needs Approval", 1],
            ["legacy-2", 1002, "Customer Two", "Buyer Two", "Sales Two", 750m, "Sent", 2]
        };
        for (var row = 0; row < sourceRows.Length; row++)
            for (var column = 0; column < sourceRows[row].Length; column++)
                source.Cell(row + 2, column + 1).Value = XLCellValue.FromObject(sourceRows[row][column]);

        var supplemental = workbook.Worksheets.Add("Table 2");
        var supplementalHeaders = new[]
        {
            "RFQ/REF No", "Estimating Rep", "RFQ Due Date", "Date to Estimating",
            "Issues?", "Quote On Track?", "Quote Complexity", "Number of Parts in Quote",
            "Estimating Status", "Estimating Completion Date", "Index"
        };
        for (var column = 0; column < supplementalHeaders.Length; column++)
            supplemental.Cell(1, column + 1).Value = supplementalHeaders[column];
        var supplementalRows = new object?[][]
        {
            ["RFQ-100", "Bethany", new DateTime(2026, 8, 27), new DateTime(2026, 8, 24), "Missing Information", "At Risk", "B (Medium)", 2, "RFQ's Sent", null, 1],
            ["RFQ-101", "Darlene", new DateTime(2026, 8, 28), new DateTime(2026, 8, 24), "None", "Yes", "C (Low)", 1, "Assembled Estimate", new DateTime(2026, 8, 25), 2]
        };
        for (var row = 0; row < supplementalRows.Length; row++)
            for (var column = 0; column < supplementalRows[row].Length; column++)
                if (supplementalRows[row][column] is not null)
                    supplemental.Cell(row + 2, column + 1).Value = XLCellValue.FromObject(supplementalRows[row][column]);

        var stream = new MemoryStream();
        workbook.SaveAs(stream);
        stream.Position = 0;
        return stream;
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly SqliteConnection connection;
        public EstimatingAccessDbContext Db { get; }
        public EstimatingHistoryImportService Importer { get; }
        public EstimatingHistoryQueryService Queries { get; }

        private Fixture(SqliteConnection connection, EstimatingAccessDbContext db)
        {
            this.connection = connection;
            Db = db;
            var reviews = new EstimatingHistoryReviewStore();
            Importer = new EstimatingHistoryImportService(db, reviews);
            Queries = new EstimatingHistoryQueryService(
                db,
                new EstimatingEstimatorSettingsService(db));
        }

        public static async Task<Fixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<EstimatingAccessDbContext>()
                .UseSqlite(connection)
                .Options;
            var db = new EstimatingAccessDbContext(options);
            await db.Database.EnsureCreatedAsync();
            return new Fixture(connection, db);
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await connection.DisposeAsync();
        }
    }
}
