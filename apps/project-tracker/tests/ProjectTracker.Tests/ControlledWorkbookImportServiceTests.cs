using System.Security.Claims;
using ClosedXML.Excel;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using ProjectTracker.Api.Data;
using ProjectTracker.Api.Models;
using ProjectTracker.Api.Services;
using ProjectTracker.Api.Services.Import;

namespace ProjectTracker.Tests;

public sealed class ControlledWorkbookImportServiceTests
{
    private const string AccountName = @"TEST\administrator";

    [Fact]
    public void PackagedTemplate_IsBlankAndContainsOnlyExpectedSheets()
    {
        using var stream = typeof(ControlledWorkbookImportService).Assembly
            .GetManifestResourceStream(ControlledWorkbookImportService.PackagedTemplateResourceName);

        Assert.NotNull(stream);
        using var workbook = new XLWorkbook(stream);
        Assert.Equal([ControlledWorkbookImportService.ProjectsSheet, ControlledWorkbookImportService.OperationsSheet],
            workbook.Worksheets.Select(sheet => sheet.Name).ToArray());
        Assert.Equal(1, workbook.Worksheet(ControlledWorkbookImportService.ProjectsSheet).LastRowUsed()!.RowNumber());
        Assert.Equal(1, workbook.Worksheet(ControlledWorkbookImportService.OperationsSheet).LastRowUsed()!.RowNumber());
    }

    [Fact]
    public async Task ExportAndValidate_RoundTripsCurrentDataWithoutChanges()
    {
        await using var connection = await OpenConnectionAsync();
        await using var db = await CreateSeededDbAsync(connection);
        var service = CreateService();

        var workbookBytes = await service.ExportTemplateAsync(db);
        using (var workbook = OpenWorkbook(workbookBytes))
        {
            Assert.Equal([ControlledWorkbookImportService.ProjectsSheet, ControlledWorkbookImportService.OperationsSheet],
                workbook.Worksheets.Select(sheet => sheet.Name).ToArray());
            Assert.Equal("Project ID (Required)", workbook.Worksheet("Projects").Cell(1, 1).GetString());
            Assert.Equal("PN-100", workbook.Worksheet("Projects").Cell(2, 2).GetString());
            Assert.Equal("Operation ID (Required)", workbook.Worksheet("Operations").Cell(1, 2).GetString());
        }

        var review = await service.ValidateAsync(db, workbookBytes, "round-trip.xlsx", AccountName);

        Assert.Empty(review.Errors);
        Assert.Empty(review.Changes);
        Assert.Equal(0, review.ChangeCount);
        Assert.False(review.CanConfirm);
        Assert.Equal("Controlled Project Tracker template", review.WorkbookFormat);
        Assert.Equal(0, review.ProjectsRequiringCompletion);
    }

    [Fact]
    public async Task Validate_ReportsRequiredFieldAndCrossSheetProjectErrors()
    {
        await using var connection = await OpenConnectionAsync();
        await using var db = await CreateSeededDbAsync(connection);
        var service = CreateService();
        var workbookBytes = await service.ExportTemplateAsync(db);

        using var workbook = OpenWorkbook(workbookBytes);
        workbook.Worksheet("Projects").Cell(2, 3).Clear();
        workbook.Worksheet("Operations").Cell(2, 1).Value = "NEW-NOT-ON-PROJECTS";
        var review = await service.ValidateAsync(db, SaveWorkbook(workbook), "errors.xlsx", AccountName);

        Assert.False(review.CanConfirm);
        Assert.Contains(review.Errors, issue => issue.Sheet == "Projects" && issue.Column == "Customer (Required)");
        Assert.Contains(review.Errors, issue => issue.Sheet == "Operations" && issue.Message.Contains("exactly match", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ValidateAndApply_AddsNewProjectAndOperationAfterConfirmation()
    {
        await using var connection = await OpenConnectionAsync();
        await using var db = await CreateSeededDbAsync(connection);
        var service = CreateService();
        var workbookBytes = await service.ExportTemplateAsync(db);

        using var workbook = OpenWorkbook(workbookBytes);
        var projects = workbook.Worksheet("Projects");
        var projectRow = projects.LastRowUsed()!.RowNumber() + 1;
        projects.Cell(projectRow, 1).Value = "NEW-PROJECT-200";
        projects.Cell(projectRow, 2).Value = "PN-200";
        projects.Cell(projectRow, 3).Value = "New Customer";

        var operations = workbook.Worksheet("Operations");
        var operationRow = operations.LastRowUsed()!.RowNumber() + 1;
        operations.Cell(operationRow, 1).Value = "NEW-PROJECT-200";
        operations.Cell(operationRow, 2).Value = "NEW-OP-10";
        operations.Cell(operationRow, 3).Value = 1;
        operations.Cell(operationRow, 4).Value = "Contract Review";
        operations.Cell(operationRow, 6).Value = "Engineering";

        var review = await service.ValidateAsync(db, SaveWorkbook(workbook), "new-project.xlsx", AccountName);

        Assert.Empty(review.Errors);
        Assert.True(review.CanConfirm);
        Assert.Equal(1, review.ProjectsAdded);
        Assert.Equal(1, review.OperationsAdded);

        var applied = await service.ApplyAsync(db, review.ReviewId, AccountName);

        Assert.Equal(1, applied.ProjectsAdded);
        var created = await db.Projects.Include(project => project.Tasks).SingleAsync(project => project.ProgramName == "PN-200");
        Assert.Equal("New Customer", created.CustomerName);
        Assert.Single(created.Tasks);
        Assert.Equal("Contract Review", created.Tasks[0].Title);
    }

    [Fact]
    public async Task Validate_AllowsDependencyOnExistingOperationOmittedFromUpload()
    {
        await using var connection = await OpenConnectionAsync();
        await using var db = await CreateSeededDbAsync(connection);
        var service = CreateService();
        var workbookBytes = await service.ExportTemplateAsync(db);

        using var workbook = OpenWorkbook(workbookBytes);
        var operations = workbook.Worksheet("Operations");
        operations.Row(2).Delete();
        operations.Cell(2, 4).Value = "Updated fabrication";
        var review = await service.ValidateAsync(db, SaveWorkbook(workbook), "partial-operations.xlsx", AccountName);

        Assert.Empty(review.Errors);
        Assert.True(review.CanConfirm);
        await service.ApplyAsync(db, review.ReviewId, AccountName);

        var updated = await db.Tasks.OrderBy(task => task.Sequence).LastAsync();
        Assert.NotNull(updated.DependencyTaskId);
        Assert.Equal("Updated fabrication", updated.Title);
    }

    [Fact]
    public async Task Validate_DoesNotBlockUnchangedLegacyDateIssueButRejectsEditedInvalidPair()
    {
        await using var connection = await OpenConnectionAsync();
        await using var db = await CreateSeededDbAsync(connection);
        var operation = await db.Tasks.OrderBy(task => task.Sequence).FirstAsync();
        operation.StartDate = new DateOnly(2026, 2, 2);
        operation.EndDate = new DateOnly(2026, 2, 1);
        await db.SaveChangesAsync();
        var service = CreateService();
        var workbookBytes = await service.ExportTemplateAsync(db);

        var unchanged = await service.ValidateAsync(db, workbookBytes, "legacy-date.xlsx", AccountName);
        Assert.DoesNotContain(unchanged.Errors, issue => issue.Message.Contains("before Start Date", StringComparison.Ordinal));

        using var workbook = OpenWorkbook(workbookBytes);
        workbook.Worksheet("Operations").Cell(2, 9).Value = new DateTime(2026, 2, 3);
        var edited = await service.ValidateAsync(db, SaveWorkbook(workbook), "edited-date.xlsx", AccountName);
        Assert.Contains(edited.Errors, issue => issue.Message.Contains("before Start Date", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ReviewWorkbook_HighlightsModifiedRows()
    {
        await using var connection = await OpenConnectionAsync();
        await using var db = await CreateSeededDbAsync(connection);
        var service = CreateService();
        var workbookBytes = await service.ExportTemplateAsync(db);

        using var workbook = OpenWorkbook(workbookBytes);
        workbook.Worksheet("Projects").Cell(2, 3).Value = "Updated Customer";
        var review = await service.ValidateAsync(db, SaveWorkbook(workbook), "review.xlsx", AccountName);
        using var highlighted = OpenWorkbook(service.BuildReviewWorkbook(review.ReviewId, AccountName));

        var projects = highlighted.Worksheet("Projects");
        Assert.Equal("CHANGED", projects.Cell(2, 15).GetString());
        Assert.Contains("Customer (Required)", projects.Cell(2, 16).GetString(), StringComparison.Ordinal);
        Assert.NotEqual(XLColor.NoColor, projects.Cell(2, 3).Style.Fill.BackgroundColor);
    }

    [Fact]
    public async Task Apply_RejectsDataChangedAfterValidation()
    {
        await using var connection = await OpenConnectionAsync();
        await using var db = await CreateSeededDbAsync(connection);
        var service = CreateService();
        var workbookBytes = await service.ExportTemplateAsync(db);

        using var workbook = OpenWorkbook(workbookBytes);
        workbook.Worksheet("Projects").Cell(2, 3).Value = "Workbook Customer";
        var review = await service.ValidateAsync(db, SaveWorkbook(workbook), "stale.xlsx", AccountName);
        var project = await db.Projects.SingleAsync();
        project.CustomerName = "Newer Live Customer";
        project.Version++;
        await db.SaveChangesAsync();

        var exception = await Assert.ThrowsAsync<ControlledImportConflictException>(
            () => service.ApplyAsync(db, review.ReviewId, AccountName));

        Assert.Contains("changed after", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("Newer Live Customer", project.CustomerName);
    }

    private static ControlledWorkbookImportService CreateService()
    {
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.Name, AccountName)],
                "Test"))
        };
        var currentUser = new CurrentUserService(new HttpContextAccessor { HttpContext = context });
        return new ControlledWorkbookImportService(
            new ControlledImportReviewStore(),
            new ProjectMetricsService(new ScheduleCalculator()),
            new ProjectAuditService(currentUser));
    }

    private static async Task<SqliteConnection> OpenConnectionAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        return connection;
    }

    private static async Task<ProjectTrackerDbContext> CreateSeededDbAsync(SqliteConnection connection)
    {
        var options = new DbContextOptionsBuilder<ProjectTrackerDbContext>()
            .UseSqlite(connection)
            .Options;
        var db = new ProjectTrackerDbContext(options);
        await db.Database.EnsureCreatedAsync();
        db.WorkCenters.Add(new WorkCenter { Name = "Engineering" });
        db.Projects.Add(new Project
        {
            ProgramName = "PN-100",
            CustomerName = "Current Customer",
            ProgramManager = "Project Lead",
            Engineer = "Engineer One",
            Version = 3,
            Tasks =
            [
                new ProjectTask
                {
                    Sequence = 1,
                    Title = "Engineering review",
                    WorkStation = "Engineering",
                    EstimatedDuration = 2,
                    Version = 4
                },
                new ProjectTask
                {
                    Sequence = 2,
                    Title = "Fabrication",
                    WorkStation = "Engineering",
                    EstimatedDuration = 3,
                    Version = 5
                }
            ]
        });
        await db.SaveChangesAsync();
        var tasks = await db.Tasks.OrderBy(task => task.Sequence).ToListAsync();
        tasks[1].DependencyTaskId = tasks[0].Id;
        await db.SaveChangesAsync();
        return db;
    }

    private static XLWorkbook OpenWorkbook(byte[] bytes) => new(new MemoryStream(bytes));

    private static byte[] SaveWorkbook(XLWorkbook workbook)
    {
        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        return stream.ToArray();
    }
}
