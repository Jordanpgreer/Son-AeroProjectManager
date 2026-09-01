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
            Assert.Equal("Operation ID (System)", workbook.Worksheet("Operations").Cell(1, 2).GetString());

            var projects = workbook.Worksheet("Projects");
            Assert.True(projects.Protection.IsProtected);
            Assert.True(projects.Cell(2, 1).Style.Protection.Locked);
            Assert.False(projects.Cell(2, 8).Style.Protection.Locked);
            Assert.False(projects.Cell(3, 1).Style.Protection.Locked);

            var operations = workbook.Worksheet("Operations");
            Assert.True(operations.Protection.IsProtected);
            Assert.True(operations.Column(2).IsHidden);
            Assert.True(operations.Cell(2, 1).Style.Protection.Locked);
            Assert.True(operations.Cell(2, 2).Style.Protection.Locked);
            Assert.False(operations.Cell(2, 3).Style.Protection.Locked);
            Assert.False(operations.Cell(4, 1).Style.Protection.Locked);
            Assert.True(operations.Cell(4, 2).Style.Protection.Locked);
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
    public async Task ExportProjectTemplate_ContainsOnlySelectedProjectAndLocksScopeColumns()
    {
        await using var connection = await OpenConnectionAsync();
        await using var db = await CreateSeededDbAsync(connection);
        var selectedProjectId = await db.Projects.Select(project => project.Id).SingleAsync();
        db.Projects.Add(new Project
        {
            ProgramName = "PN-OTHER",
            CustomerName = "Other Customer",
            Tasks = [new ProjectTask { Sequence = 1, Title = "Other operation" }]
        });
        await db.SaveChangesAsync();
        var service = CreateService();

        var workbookBytes = await service.ExportProjectTemplateAsync(db, selectedProjectId);
        using var workbook = OpenWorkbook(workbookBytes);
        var projects = workbook.Worksheet(ControlledWorkbookImportService.ProjectsSheet);
        var operations = workbook.Worksheet(ControlledWorkbookImportService.OperationsSheet);

        Assert.Equal(2, projects.LastRowUsed()!.RowNumber());
        Assert.Equal(selectedProjectId, projects.Cell(2, 1).GetValue<int>());
        Assert.Equal(3, operations.LastRowUsed()!.RowNumber());
        Assert.All(operations.RowsUsed().Skip(1), row =>
            Assert.Equal(selectedProjectId, row.Cell(1).GetValue<int>()));
        Assert.True(operations.Column(1).IsHidden);
        Assert.True(operations.Column(2).IsHidden);
        Assert.True(operations.Cell(4, 1).Style.Protection.Locked);
        Assert.True(operations.Cell(4, 2).Style.Protection.Locked);
        Assert.False(operations.Cell(4, 3).Style.Protection.Locked);
    }

    [Fact]
    public async Task ValidateAndApplyProjectBom_UpdatesDatesAndAddsOperationWithoutUserManagedIds()
    {
        await using var connection = await OpenConnectionAsync();
        await using var db = await CreateSeededDbAsync(connection);
        var projectId = await db.Projects.Select(project => project.Id).SingleAsync();
        var service = CreateService();
        var workbookBytes = await service.ExportProjectTemplateAsync(db, projectId);

        using var workbook = OpenWorkbook(workbookBytes);
        var operations = workbook.Worksheet(ControlledWorkbookImportService.OperationsSheet);
        operations.Cell(2, 8).Value = "Yes";
        operations.Cell(2, 9).Value = new DateTime(2026, 9, 8);
        operations.Cell(2, 10).Value = new DateTime(2026, 9, 7);
        operations.Cell(2, 11).Value = new DateTime(2026, 9, 9);
        operations.Cell(2, 12).Value = new DateTime(2026, 9, 8);
        operations.Cell(4, 3).Value = 3;
        operations.Cell(4, 4).Value = "Final inspection";
        operations.Cell(4, 6).Value = "Engineering";
        operations.Cell(4, 8).Value = "Yes";
        operations.Cell(4, 9).Value = new DateTime(2026, 9, 10);
        operations.Cell(4, 10).Value = new DateTime(2026, 9, 9);
        operations.Cell(4, 11).Value = new DateTime(2026, 9, 10);
        operations.Cell(4, 12).Value = new DateTime(2026, 9, 9);
        operations.Cell(4, 13).Value = 1;

        var review = await service.ValidateProjectAsync(
            db,
            projectId,
            SaveWorkbook(workbook),
            "project-bom.xlsx",
            AccountName);

        Assert.Empty(review.Errors);
        Assert.True(review.CanConfirm);
        Assert.Equal("Project BOM template", review.WorkbookFormat);
        Assert.Equal(1, review.OperationsAdded);
        Assert.Equal(1, review.OperationsUpdated);
        Assert.StartsWith($"/api/projects/{projectId}/bom/reviews/", review.ReviewWorkbookUrl, StringComparison.Ordinal);
        await Assert.ThrowsAsync<ControlledImportValidationException>(
            () => service.ApplyAsync(db, review.ReviewId, AccountName));

        var applied = await service.ApplyProjectAsync(db, projectId, review.ReviewId, AccountName);

        Assert.Equal(1, applied.OperationsAdded);
        Assert.Equal(1, applied.OperationsUpdated);
        var tasks = await db.Tasks.OrderBy(task => task.Sequence).ToListAsync();
        Assert.Equal(3, tasks.Count);
        Assert.Equal(new DateOnly(2026, 9, 8), tasks[0].StartDate);
        Assert.Equal(new DateOnly(2026, 9, 7), tasks[0].OriginalStartDate);
        Assert.Equal(new DateOnly(2026, 9, 9), tasks[0].EndDate);
        Assert.Equal(new DateOnly(2026, 9, 8), tasks[0].OriginalEndDate);
        Assert.Equal("Final inspection", tasks[2].Title);
        Assert.True(tasks[2].Id > 0);
    }

    [Fact]
    public async Task ValidateProjectBom_RejectsRecordsForAnotherProject()
    {
        await using var connection = await OpenConnectionAsync();
        await using var db = await CreateSeededDbAsync(connection);
        var selectedProjectId = await db.Projects.Select(project => project.Id).SingleAsync();
        var otherProject = new Project
        {
            ProgramName = "PN-OTHER",
            CustomerName = "Other Customer"
        };
        db.Projects.Add(otherProject);
        await db.SaveChangesAsync();
        var service = CreateService();
        var workbookBytes = await service.ExportProjectTemplateAsync(db, selectedProjectId);

        using var workbook = OpenWorkbook(workbookBytes);
        var projects = workbook.Worksheet(ControlledWorkbookImportService.ProjectsSheet);
        projects.Cell(3, 1).Value = otherProject.Id;
        projects.Cell(3, 2).Value = otherProject.ProgramName;
        projects.Cell(3, 3).Value = otherProject.CustomerName;
        var operations = workbook.Worksheet(ControlledWorkbookImportService.OperationsSheet);
        operations.Cell(4, 1).Value = otherProject.Id;
        operations.Cell(4, 3).Value = 1;
        operations.Cell(4, 4).Value = "Unauthorized cross-project operation";

        var review = await service.ValidateProjectAsync(
            db,
            selectedProjectId,
            SaveWorkbook(workbook),
            "wrong-project.xlsx",
            AccountName);

        Assert.False(review.CanConfirm);
        Assert.Contains(review.Errors, issue =>
            issue.Message.Contains($"limited to Project ID {selectedProjectId}", StringComparison.Ordinal));
        Assert.Contains(review.Errors, issue =>
            issue.Sheet == ControlledWorkbookImportService.OperationsSheet
            && issue.Message.Contains($"belong to Project ID {selectedProjectId}", StringComparison.Ordinal));
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
    public async Task ValidateAndApply_AllowsBlankSystemOperationIds()
    {
        await using var connection = await OpenConnectionAsync();
        await using var db = await CreateSeededDbAsync(connection);
        var service = CreateService();
        var workbookBytes = await service.ExportTemplateAsync(db);

        using var workbook = OpenWorkbook(workbookBytes);
        var operations = workbook.Worksheet("Operations");
        operations.Cell(2, 2).Clear();
        operations.Cell(2, 16).Value = "Matched without a visible operation ID";
        operations.Cell(4, 1).Value = await db.Projects.Select(project => project.Id).SingleAsync();
        operations.Cell(4, 3).Value = 3;
        operations.Cell(4, 4).Value = "New operation without an ID";
        operations.Cell(4, 6).Value = "Engineering";

        var review = await service.ValidateAsync(db, SaveWorkbook(workbook), "automatic-operation-ids.xlsx", AccountName);

        Assert.Empty(review.Errors);
        Assert.Equal(1, review.OperationsAdded);
        Assert.Equal(1, review.OperationsUpdated);

        await service.ApplyAsync(db, review.ReviewId, AccountName);

        var tasks = await db.Tasks.OrderBy(task => task.Sequence).ToListAsync();
        Assert.Equal(3, tasks.Count);
        Assert.Equal("Matched without a visible operation ID", tasks[0].Notes);
        Assert.Equal("New operation without an ID", tasks[2].Title);
        Assert.True(tasks[2].Id > 0);
    }

    [Fact]
    public async Task ValidateAndApply_BulkUpdatesProjectPriorities()
    {
        await using var connection = await OpenConnectionAsync();
        await using var db = await CreateSeededDbAsync(connection);
        db.Projects.Add(new Project
        {
            ProgramName = "PN-200",
            CustomerName = "Second Customer"
        });
        await db.SaveChangesAsync();
        var service = CreateService();
        var workbookBytes = await service.ExportTemplateAsync(db);

        using var workbook = OpenWorkbook(workbookBytes);
        var projects = workbook.Worksheet("Projects");
        projects.Cell(2, 8).Value = 2;
        projects.Cell(3, 8).Value = 1;

        var review = await service.ValidateAsync(db, SaveWorkbook(workbook), "priority-update.xlsx", AccountName);

        Assert.Empty(review.Errors);
        Assert.Equal(2, review.ProjectsUpdated);
        Assert.Contains(review.Changes, change => change.Field == "Priority");

        await service.ApplyAsync(db, review.ReviewId, AccountName);

        Assert.Equal(2, await db.Projects.Where(project => project.ProgramName == "PN-100").Select(project => project.PriorityRank).SingleAsync());
        Assert.Equal(1, await db.Projects.Where(project => project.ProgramName == "PN-200").Select(project => project.PriorityRank).SingleAsync());
    }

    [Fact]
    public async Task Validate_AcceptsLegacyRequiredOperationIdHeader()
    {
        await using var connection = await OpenConnectionAsync();
        await using var db = await CreateSeededDbAsync(connection);
        var service = CreateService();
        var workbookBytes = await service.ExportTemplateAsync(db);

        using var workbook = OpenWorkbook(workbookBytes);
        workbook.Worksheet("Operations").Cell(1, 2).Value = "Operation ID (Required)";
        var review = await service.ValidateAsync(db, SaveWorkbook(workbook), "legacy-header.xlsx", AccountName);

        Assert.DoesNotContain(review.Errors, issue => issue.Row == 1 && issue.Column == "Operation ID (System)");
    }

    [Fact]
    public async Task ValidateAndApply_CreatesProjectsForUnknownNumericIds()
    {
        await using var connection = await OpenConnectionAsync();
        await using var db = await CreateSeededDbAsync(connection);
        var service = CreateService();
        using var templateStream = typeof(ControlledWorkbookImportService).Assembly
            .GetManifestResourceStream(ControlledWorkbookImportService.PackagedTemplateResourceName);
        Assert.NotNull(templateStream);
        using var workbook = new XLWorkbook(templateStream);

        var projects = workbook.Worksheet("Projects");
        projects.Cell(2, 1).Value = 500;
        projects.Cell(2, 2).Value = "PORTABLE-500";
        projects.Cell(2, 3).Value = "Portable Customer";
        projects.Cell(3, 1).Value = 501;
        projects.Cell(3, 2).Value = "PORTABLE-501";
        projects.Cell(3, 3).Value = "Portable Customer";

        var operations = workbook.Worksheet("Operations");
        operations.Cell(2, 1).Value = 500;
        operations.Cell(2, 2).Value = 900;
        operations.Cell(2, 3).Value = 1;
        operations.Cell(2, 4).Value = "First routing step";
        operations.Cell(2, 6).Value = "External Center";
        operations.Cell(3, 1).Value = 500;
        operations.Cell(3, 2).Value = 901;
        operations.Cell(3, 3).Value = 1;
        operations.Cell(3, 4).Value = "Second routing step";
        operations.Cell(3, 6).Value = "External Center";
        operations.Cell(4, 1).Value = 501;
        operations.Cell(4, 2).Value = 902;
        operations.Cell(4, 3).Value = 1;
        operations.Cell(4, 4).Value = "Other project step";

        var review = await service.ValidateAsync(db, SaveWorkbook(workbook), "portable.xlsx", AccountName);

        Assert.Empty(review.Errors);
        Assert.True(review.CanConfirm);
        Assert.Equal(2, review.ProjectsAdded);
        Assert.Equal(3, review.OperationsAdded);
        Assert.Contains("new numeric project IDs", review.WorkbookFormat, StringComparison.OrdinalIgnoreCase);

        await service.ApplyAsync(db, review.ReviewId, AccountName);

        var imported = await db.Projects
            .Include(project => project.Tasks)
            .SingleAsync(project => project.ProgramName == "PORTABLE-500");
        Assert.Equal([1, 2], imported.Tasks.OrderBy(task => task.Sequence).Select(task => task.Sequence).ToArray());
        Assert.All(imported.Tasks, task => Assert.Equal("External Center", task.WorkStation));
    }

    [Fact]
    public async Task ValidateAndApply_CreatesProjectForAnIsolatedUnknownNumericId()
    {
        await using var connection = await OpenConnectionAsync();
        await using var db = await CreateSeededDbAsync(connection);
        var service = CreateService();
        var workbookBytes = await service.ExportTemplateAsync(db);

        using var workbook = OpenWorkbook(workbookBytes);
        workbook.Worksheet("Projects").Cell(2, 1).Value = 999;
        workbook.Worksheet("Projects").Cell(2, 2).Value = "PN-999";
        workbook.Worksheet("Operations").Cell(2, 1).Value = 999;
        workbook.Worksheet("Operations").Cell(3, 1).Value = 999;
        var review = await service.ValidateAsync(db, SaveWorkbook(workbook), "stale-id.xlsx", AccountName);

        Assert.Empty(review.Errors);
        Assert.Equal(1, review.ProjectsAdded);
        Assert.Equal(2, review.OperationsAdded);

        await service.ApplyAsync(db, review.ReviewId, AccountName);

        var imported = await db.Projects
            .Include(project => project.Tasks)
            .SingleAsync(project => project.ProgramName == "PN-999");
        Assert.Equal(2, imported.Tasks.Count);
    }

    [Fact]
    public async Task ValidateAndApply_ResolvesKnownAndUnknownProjectIdsIndependently()
    {
        await using var connection = await OpenConnectionAsync();
        await using var db = await CreateSeededDbAsync(connection);
        var existingProjectId = await db.Projects.Select(project => project.Id).SingleAsync();
        var service = CreateService();
        var workbookBytes = await service.ExportTemplateAsync(db);

        using var workbook = OpenWorkbook(workbookBytes);
        var projects = workbook.Worksheet("Projects");
        projects.Cell(3, 1).Value = 999;
        projects.Cell(3, 2).Value = "PN-999";
        projects.Cell(3, 3).Value = "New Customer";
        var operations = workbook.Worksheet("Operations");
        operations.Cell(4, 1).Value = 999;
        operations.Cell(4, 2).Value = 999;
        operations.Cell(4, 3).Value = 1;
        operations.Cell(4, 4).Value = "New project operation";

        var review = await service.ValidateAsync(db, SaveWorkbook(workbook), "mixed-ids.xlsx", AccountName);

        Assert.Empty(review.Errors);
        Assert.Equal(1, review.ProjectsAdded);
        Assert.Equal(1, review.OperationsAdded);

        await service.ApplyAsync(db, review.ReviewId, AccountName);

        Assert.NotNull(await db.Projects.FindAsync(existingProjectId));
        var imported = await db.Projects
            .Include(project => project.Tasks)
            .SingleAsync(project => project.ProgramName == "PN-999");
        Assert.Single(imported.Tasks);
        Assert.Equal("New project operation", imported.Tasks[0].Title);
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
