using Microsoft.EntityFrameworkCore;
using ProjectTracker.Api.Data;
using ProjectTracker.Api.Models;

namespace ProjectTracker.Api.Services.Reports;

public sealed class ReportService(ProjectTrackerDbContext db)
{
    private const string XlsxContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";
    private const string PdfContentType = "application/pdf";

    public async Task<ReportFile> PortfolioExcelAsync(CancellationToken cancellationToken = default)
    {
        var data = await LoadPortfolioAsync(cancellationToken);
        var content = ExcelReportBuilder.BuildPortfolio(data.Projects, data.Calendar, ReportAssets.LogoPath);
        return new ReportFile(content, XlsxContentType, $"portfolio-summary-{DateOnly.FromDateTime(DateTime.Today):yyyyMMdd}.xlsx");
    }

    public async Task<ReportFile> ProjectExcelAsync(int projectId, CancellationToken cancellationToken = default)
    {
        var data = await LoadProjectAsync(projectId, cancellationToken);
        var content = ExcelReportBuilder.BuildProject(data.Project, data.Calendar, ReportAssets.LogoPath);
        return new ReportFile(content, XlsxContentType, $"{SafeName(data.Project.ProgramName)}-schedule.xlsx");
    }

    public async Task<ReportFile> PortfolioPdfAsync(CancellationToken cancellationToken = default)
    {
        var data = await LoadPortfolioAsync(cancellationToken);
        var content = PdfReportBuilder.BuildPortfolio(data.Projects, data.Calendar, ReportAssets.LogoPath);
        return new ReportFile(content, PdfContentType, $"portfolio-summary-{DateOnly.FromDateTime(DateTime.Today):yyyyMMdd}.pdf");
    }

    public async Task<ReportFile> ProjectPdfAsync(int projectId, CancellationToken cancellationToken = default)
    {
        var data = await LoadProjectAsync(projectId, cancellationToken);
        var content = PdfReportBuilder.BuildProject(data.Project, data.Calendar, ReportAssets.LogoPath);
        return new ReportFile(content, PdfContentType, $"{SafeName(data.Project.ProgramName)}-schedule.pdf");
    }

    private async Task<(IReadOnlyList<Project> Projects, ScheduleCalendar Calendar)> LoadPortfolioAsync(CancellationToken cancellationToken)
    {
        var projects = await db.Projects
            .Include(project => project.Tasks)
            .ThenInclude(task => task.OvertimeDays)
            .OrderBy(project => project.TargetDelivery)
            .ThenBy(project => project.ProgramName)
            .ToListAsync(cancellationToken);
        return (projects, await LoadCalendarAsync(cancellationToken));
    }

    private async Task<(Project Project, ScheduleCalendar Calendar)> LoadProjectAsync(int projectId, CancellationToken cancellationToken)
    {
        var project = await db.Projects
            .Include(project => project.Tasks)
            .ThenInclude(task => task.OvertimeDays)
            .FirstOrDefaultAsync(project => project.Id == projectId, cancellationToken)
            ?? throw new KeyNotFoundException("Project not found.");
        return (project, await LoadCalendarAsync(cancellationToken));
    }

    private async Task<ScheduleCalendar> LoadCalendarAsync(CancellationToken cancellationToken)
    {
        var settings = await db.ScheduleSettings.FindAsync([ScheduleSettings.SingletonId], cancellationToken)
            ?? new ScheduleSettings();
        var holidays = (await db.Holidays.Select(holiday => holiday.Date).ToListAsync(cancellationToken)).ToHashSet();
        return new ScheduleCalendar(settings.GetWorkingDays(), holidays);
    }

    private static string SafeName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        return new string(value.Select(character => invalid.Contains(character) ? '-' : character).ToArray());
    }
}

internal static class ReportAssets
{
    public static string? LogoPath
    {
        get
        {
            var path = Path.Combine(AppContext.BaseDirectory, "Assets", "son-aero-report-logo.jpg");
            return File.Exists(path) ? path : null;
        }
    }
}

internal static class ReportText
{
    public static string Status(ProjectStatus status) => status switch
    {
        ProjectStatus.OnTrack => "On Track",
        ProjectStatus.NotStarted => "Not Started",
        _ => status.ToString()
    };

    public static string Status(TaskScheduleStatus status) => status switch
    {
        TaskScheduleStatus.OnTrack => "On Track",
        TaskScheduleStatus.NotStarted => "Not Started",
        _ => status.ToString()
    };

    public static string Percent(decimal value) => $"{Math.Round(value * 100m, 0)}%";
    public static string Date(DateOnly? value) => value?.ToString("MMM d, yyyy") ?? "Not set";
    public static DateTime? DateValue(DateOnly? value) => value?.ToDateTime(TimeOnly.MinValue);
}
