using System.Security.Claims;
using ClosedXML.Excel;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using ProjectTracker.Api.Data;
using ProjectTracker.Api.Mapping;
using ProjectTracker.Api.Models;
using ProjectTracker.Api.Services;
using ProjectTracker.Api.Services.Import;

namespace ProjectTracker.Tests;

public sealed class LegacyProjectWorkbookImportTests
{
    private const string AccountName = @"TEST\administrator";

    [Fact]
    public async Task SingleProjectGanttSchedule_CreatesIncompleteProjectAndOperations()
    {
        await using var connection = await OpenConnectionAsync();
        await using var db = await CreateDbAsync(connection);
        var service = CreateService();
        var workbookBytes = CreateSingleProjectWorkbook();

        var review = await service.ValidateAsync(
            db,
            workbookBytes,
            "1TD1351A10015-1008 Schedule.xlsx",
            AccountName);

        Assert.Empty(review.Errors);
        Assert.True(review.CanConfirm);
        Assert.Equal("Legacy single-project Gantt schedule", review.WorkbookFormat);
        Assert.Equal(1, review.ProjectsAdded);
        Assert.Equal(2, review.OperationsAdded);
        Assert.Equal(1, review.ProjectsRequiringCompletion);

        using (var normalized = new XLWorkbook(new MemoryStream(service.BuildReviewWorkbook(review.ReviewId, AccountName))))
        {
            Assert.Equal(["Projects", "Operations"], normalized.Worksheets.Select(sheet => sheet.Name).ToArray());
            Assert.Equal("1TD1351A10015-1008", normalized.Worksheet("Projects").Cell(2, 2).GetString());
            Assert.Equal("NEW-OP-1", normalized.Worksheet("Operations").Cell(3, 7).GetString());
        }

        await service.ApplyAsync(db, review.ReviewId, AccountName);

        var project = await db.Projects
            .Include(candidate => candidate.Tasks)
            .SingleAsync(candidate => candidate.ProgramName == "1TD1351A10015-1008");
        Assert.True(project.ImportNeedsCompletion);
        Assert.Equal(2, project.Tasks.Count);
        Assert.Equal("Kickoff", project.Tasks[0].Title);
        Assert.Equal(project.Tasks[0].Id, project.Tasks[1].DependencyTaskId);
        Assert.Equal(new DateOnly(2026, 8, 4), project.Tasks[0].StartDate);
    }

    [Fact]
    public async Task MultiProjectTrackerWorkbook_CreatesEveryProjectWithoutTemplateChanges()
    {
        await using var connection = await OpenConnectionAsync();
        await using var db = await CreateDbAsync(connection);
        var service = CreateService();
        var workbookBytes = CreateMultiProjectWorkbook("PN-200", "PN-201");

        var review = await service.ValidateAsync(db, workbookBytes, "Project Tracker.xlsm", AccountName);

        Assert.Empty(review.Errors);
        Assert.True(review.CanConfirm);
        Assert.Equal("Legacy multi-project tracker workbook", review.WorkbookFormat);
        Assert.Equal(2, review.ProjectsAdded);
        Assert.Equal(4, review.OperationsAdded);
        Assert.Equal(2, review.ProjectsRequiringCompletion);

        await service.ApplyAsync(db, review.ReviewId, AccountName);

        var imported = await db.Projects
            .Where(project => project.ProgramName == "PN-200" || project.ProgramName == "PN-201")
            .Include(project => project.Tasks)
            .OrderBy(project => project.ProgramName)
            .ToListAsync();
        Assert.Equal(2, imported.Count);
        Assert.All(imported, project => Assert.True(project.ImportNeedsCompletion));
        Assert.All(imported, project => Assert.Equal(2, project.Tasks.Count));
        Assert.All(imported, project => Assert.Equal("Manufacturing", project.Tasks[1].Phase));
    }

    [Fact]
    public async Task LegacyWorkbook_CannotOverwriteExistingPartNumber()
    {
        await using var connection = await OpenConnectionAsync();
        await using var db = await CreateDbAsync(connection);
        db.Projects.Add(new Project
        {
            ProgramName = "PN-100",
            CustomerName = "Existing customer",
            ProgramManager = "Existing lead",
        });
        await db.SaveChangesAsync();
        var service = CreateService();

        var review = await service.ValidateAsync(
            db,
            CreateMultiProjectWorkbook("PN-100"),
            "Project Tracker.xlsm",
            AccountName);

        Assert.False(review.CanConfirm);
        Assert.Contains(review.Errors, issue => issue.Message.Contains("already belongs", StringComparison.Ordinal));
        Assert.Equal("Existing customer", (await db.Projects.SingleAsync()).CustomerName);
    }

    [Fact]
    public void CompletionFlag_ClearsOnlyAfterAllImportedFieldsAreProvided()
    {
        var project = new Project
        {
            ProgramName = "PN-300",
            ImportNeedsCompletion = true,
            CustomerName = "Customer",
            ProgramManager = "Lead",
            Engineer = "Engineer",
            SalesOrderNumber = "SO-1",
        };

        ProjectImportCompletion.Refresh(project);
        Assert.True(project.ImportNeedsCompletion);
        Assert.Equal(["Job Number"], ProjectImportCompletion.GetMissingFields(project).Select(field => field.Label));

        project.JobNumber = "JOB-1";
        ProjectImportCompletion.Refresh(project);
        Assert.False(project.ImportNeedsCompletion);
        Assert.Empty(ProjectDtoMapper.ToDetailDto(project).MissingImportFields);
    }

    private static byte[] CreateSingleProjectWorkbook()
    {
        using var workbook = new XLWorkbook();
        var sheet = workbook.AddWorksheet("Project Gantt Chart");
        sheet.Cell(1, 1).Value = "1TD1351A10015-1008 Schedule";
        WriteRow(sheet, 5, 1,
            "ID", "Task Title", "Phase", "Start Date", "End Date", "Original Start Date",
            "Original End Date", "Duration (days)", "% Complete", "Dependency", "Late To Schedule", "Notes");
        WriteRow(sheet, 6, 1,
            "10", "Kickoff", "Planning", new DateTime(2026, 8, 4), new DateTime(2026, 8, 5),
            new DateTime(2026, 8, 4), new DateTime(2026, 8, 5), 2, 0.25, null, null, "Initial review");
        WriteRow(sheet, 7, 1,
            "20", "Engineering", "Engineering", new DateTime(2026, 8, 6), new DateTime(2026, 8, 10),
            new DateTime(2026, 8, 6), new DateTime(2026, 8, 10), 3, 50, "10", null, null);
        return SaveWorkbook(workbook);
    }

    private static byte[] CreateMultiProjectWorkbook(params string[] partNumbers)
    {
        using var workbook = new XLWorkbook();
        foreach (var partNumber in partNumbers)
        {
            var sheet = workbook.AddWorksheet(partNumber);
            sheet.Cell(2, 2).Value = partNumber;
            WriteRow(sheet, 8, 2,
                "ID", "Task Title", "Phase", "Start Date", "Orig Start", "End Date", "Orig End",
                "Estimated Duration", "Actual Duration", "% Complete", "Status", "Notes");
            WriteRow(sheet, 9, 2,
                "1", "Planning", "Planning", new DateTime(2026, 8, 4), new DateTime(2026, 8, 4),
                new DateTime(2026, 8, 5), new DateTime(2026, 8, 5), 2, null, 0, "Not Started", "Plan");
            WriteRow(sheet, 10, 2,
                "2", "Build", "Manufacturing", new DateTime(2026, 8, 6), new DateTime(2026, 8, 6),
                new DateTime(2026, 8, 8), new DateTime(2026, 8, 8), 3, 1, 0.5, "In Progress", "Build");
            sheet.Cell(11, 3).Value = "ADD";
        }
        workbook.AddWorksheet("Holiday Schedule");
        return SaveWorkbook(workbook);
    }

    private static void WriteRow(IXLWorksheet sheet, int row, int startColumn, params object?[] values)
    {
        for (var index = 0; index < values.Length; index++)
        {
            var value = values[index];
            if (value is not null) sheet.Cell(row, startColumn + index).Value = XLCellValue.FromObject(value);
        }
    }

    private static byte[] SaveWorkbook(XLWorkbook workbook)
    {
        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        return stream.ToArray();
    }

    private static ControlledWorkbookImportService CreateService()
    {
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, AccountName)], "Test"))
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

    private static async Task<ProjectTrackerDbContext> CreateDbAsync(SqliteConnection connection)
    {
        var options = new DbContextOptionsBuilder<ProjectTrackerDbContext>().UseSqlite(connection).Options;
        var db = new ProjectTrackerDbContext(options);
        await db.Database.EnsureCreatedAsync();
        return db;
    }
}
