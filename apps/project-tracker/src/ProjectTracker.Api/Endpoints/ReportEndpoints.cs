using ProjectTracker.Api.Services.Reports;

namespace ProjectTracker.Api.Endpoints;

public static class ReportEndpoints
{
    public static RouteGroupBuilder MapReportEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/reports/portfolio.xlsx", async (ReportService reports, CancellationToken cancellationToken) =>
            File(await reports.PortfolioExcelAsync(cancellationToken)));
        api.MapGet("/reports/portfolio.pdf", async (ReportService reports, CancellationToken cancellationToken) =>
            File(await reports.PortfolioPdfAsync(cancellationToken)));
        api.MapGet("/reports/past-projects.xlsx", async (ReportService reports, CancellationToken cancellationToken) =>
            File(await reports.PastProjectsExcelAsync(cancellationToken)));
        api.MapGet("/reports/past-projects.pdf", async (ReportService reports, CancellationToken cancellationToken) =>
            File(await reports.PastProjectsPdfAsync(cancellationToken)));

        api.MapGet("/reports/projects/{id:int}.xlsx", (int id, ReportService reports, CancellationToken cancellationToken) =>
            ProjectFileAsync(() => reports.ProjectExcelAsync(id, cancellationToken)));
        api.MapGet("/reports/projects/{id:int}.pdf", (int id, ReportService reports, CancellationToken cancellationToken) =>
            ProjectFileAsync(() => reports.ProjectPdfAsync(id, cancellationToken)));
        api.MapGet("/reports/projects/{id:int}/activity.pdf", (int id, ReportService reports, CancellationToken cancellationToken) =>
            ProjectFileAsync(() => reports.ProjectActivityPdfAsync(id, cancellationToken)))
            .RequireAuthorization("ProjectActivityView");
        return api;
    }

    private static IResult File(ReportFile report) => Results.File(report.Content, report.ContentType, report.FileName);

    private static async Task<IResult> ProjectFileAsync(Func<Task<ReportFile>> build)
    {
        try
        {
            return File(await build());
        }
        catch (KeyNotFoundException)
        {
            return Results.NotFound();
        }
    }
}
