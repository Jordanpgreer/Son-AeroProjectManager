using Microsoft.AspNetCore.Mvc;
using ProjectTracker.Api.Data;
using ProjectTracker.Api.Services.Import;

namespace ProjectTracker.Api.Endpoints;

public static class WorkCenterImportEndpoints
{
    public const string AuthorizationPolicy = "ImportWorkCenters";

    public static RouteGroupBuilder MapWorkCenterImportEndpoints(this RouteGroupBuilder api)
    {
        api.MapPost("/work-centers/import", ImportAsync)
            .RequireAuthorization(AuthorizationPolicy)
            .DisableAntiforgery()
            .WithMetadata(new RequestSizeLimitAttribute(WorkCenterWorkbookImportService.MaxWorkbookBytes + 128 * 1024));
        return api;
    }

    public static async Task<IResult> ImportAsync(
        HttpRequest request,
        IFormFile file,
        ProjectTrackerDbContext db,
        WorkCenterWorkbookImportService importer,
        CancellationToken cancellationToken)
    {
        if (!request.HasFormContentType
            || !string.Equals(
                request.Headers["X-Requested-With"].ToString(),
                "XMLHttpRequest",
                StringComparison.OrdinalIgnoreCase))
        {
            return Results.BadRequest("Work-center imports must be submitted from the Project Tracker admin screen.");
        }
        if (file is null || file.Length == 0)
        {
            return Results.BadRequest("Choose a non-empty .xlsx workbook to upload.");
        }
        if (file.Length > WorkCenterWorkbookImportService.MaxWorkbookBytes)
        {
            return Results.BadRequest("The workbook is larger than the 5 MB import limit.");
        }
        if (!string.Equals(Path.GetExtension(file.FileName), ".xlsx", StringComparison.OrdinalIgnoreCase))
        {
            return Results.BadRequest("Upload a supported .xlsx workbook.");
        }

        try
        {
            await using var stream = file.OpenReadStream();
            return Results.Ok(await importer.ImportAsync(db, stream, cancellationToken));
        }
        catch (WorkCenterWorkbookImportException exception)
        {
            return Results.BadRequest(exception.Message);
        }
    }
}
