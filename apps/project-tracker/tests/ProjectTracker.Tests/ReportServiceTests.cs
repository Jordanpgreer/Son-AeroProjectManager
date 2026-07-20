using ClosedXML.Excel;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PdfSharp.Pdf.IO;
using ProjectTracker.Api.Data;
using ProjectTracker.Api.Models;
using ProjectTracker.Api.Services.Reports;

namespace ProjectTracker.Tests;

public sealed class ReportServiceTests
{
    [Fact]
    public async Task ProjectExports_IncludeBrandedSummaryAndGanttContent()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<ProjectTrackerDbContext>().UseSqlite(connection).Options;
        await using var db = new ProjectTrackerDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var project = new Project
        {
            ProgramName = "TEST-1001",
            CustomerName = "Test Customer",
            ProgramManager = "Program Manager",
            ProgramStart = new DateOnly(2026, 6, 22),
            TargetDelivery = new DateOnly(2026, 7, 8),
            Progress = 0.5m,
            Status = ProjectStatus.OnTrack,
            CurrentTask = "CNC Production",
            Tasks =
            [
                new ProjectTask
                {
                    Sequence = 1,
                    Title = "CNC Production",
                    WorkStation = "CNC Mill",
                    StartDate = new DateOnly(2026, 6, 22),
                    EndDate = new DateOnly(2026, 7, 8),
                    EstimatedDuration = 10,
                    PercentComplete = 0.5m,
                    Status = TaskScheduleStatus.OnTrack
                }
            ]
        };
        db.Projects.Add(project);
        db.ScheduleSettings.Add(new ScheduleSettings());
        await db.SaveChangesAsync();

        var reports = new ReportService(db);
        var excel = await reports.ProjectExcelAsync(project.Id);
        using (var workbook = new XLWorkbook(new MemoryStream(excel.Content)))
        {
            Assert.Equal(2, workbook.Worksheets.Count);
            Assert.NotNull(workbook.Worksheet("Project Summary"));
            var timeline = workbook.Worksheet("Gantt Timeline");
            Assert.Equal("TEST-1001 Timeline", timeline.Cell("A5").GetString());
            Assert.Equal("CNC Production", timeline.Cell("B10").GetString());
        }

        var pdf = await reports.ProjectPdfAsync(project.Id);
        using var document = PdfReader.Open(new MemoryStream(pdf.Content), PdfDocumentOpenMode.Import);
        Assert.True(document.PageCount >= 2);
        Assert.Equal("TEST-1001 Project Schedule", document.Info.Title);
    }
}
