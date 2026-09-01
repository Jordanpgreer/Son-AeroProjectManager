using ProjectTracker.Api.Data;
using ProjectTracker.Api.Dtos;
using ProjectTracker.Api.Services;
using ProjectTracker.Api.Services.Import;

namespace ProjectTracker.Api.Endpoints;

public static class ImportEndpoints
{
    public static RouteGroupBuilder MapImportEndpoints(this RouteGroupBuilder api)
    {
        api.MapPost("/import/workbook", async (
            ImportWorkbookRequest request,
            IConfiguration configuration,
            IWebHostEnvironment environment,
            ProjectTrackerDbContext db,
            WorkbookImportService importer,
            CancellationToken cancellationToken) =>
        {
            var path = WorkbookPathResolver.Resolve(request.Path, configuration, environment);
            return Results.Ok(await importer.ImportAsync(db, path, request.ReplaceExisting, cancellationToken));
        }).RequireAuthorization("ManageImports");

        api.MapGet("/import/template", async (
            ProjectTrackerDbContext db,
            ControlledWorkbookImportService controlledImport,
            CancellationToken cancellationToken) =>
        {
            var workbook = await controlledImport.ExportTemplateAsync(db, cancellationToken);
            return Results.File(
                workbook,
                "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                ControlledWorkbookImportService.TemplateFileName);
        }).RequireAuthorization("ManageImports");

        api.MapPost("/import/validate", ValidateAsync)
            .RequireAuthorization("ManageImports")
            .DisableAntiforgery();
        api.MapPost("/import/upload", ValidateAsync)
            .RequireAuthorization("ManageImports")
            .DisableAntiforgery();
        api.MapGet("/import/reviews/{reviewId}/workbook", (
            string reviewId,
            CurrentUserService currentUser,
            ControlledWorkbookImportService controlledImport) =>
        {
            try
            {
                var workbook = controlledImport.BuildReviewWorkbook(reviewId, currentUser.AccountName);
                return Results.File(
                    workbook,
                    "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                    $"Project-Tracker-Import-Review-{reviewId[..Math.Min(8, reviewId.Length)]}.xlsx");
            }
            catch (ControlledImportValidationException exception)
            {
                return Results.BadRequest(exception.Message);
            }
        }).RequireAuthorization("ManageImports");
        api.MapPost("/import/reviews/{reviewId}/confirm", ConfirmAsync)
            .RequireAuthorization("ManageImports");

        api.MapGet("/projects/{projectId:int}/bom/template", DownloadProjectTemplateAsync)
            .RequireAuthorization("ManageImports");
        api.MapPost("/projects/{projectId:int}/bom/validate", ValidateProjectBomAsync)
            .RequireAuthorization("ManageImports")
            .DisableAntiforgery();
        api.MapGet("/projects/{projectId:int}/bom/reviews/{reviewId}/workbook", DownloadProjectReviewAsync)
            .RequireAuthorization("ManageImports");
        api.MapPost("/projects/{projectId:int}/bom/reviews/{reviewId}/confirm", ConfirmProjectBomAsync)
            .RequireAuthorization("ManageImports");
        return api;
    }

    private static async Task<IResult> DownloadProjectTemplateAsync(
        int projectId,
        ProjectTrackerDbContext db,
        ControlledWorkbookImportService controlledImport,
        CancellationToken cancellationToken)
    {
        try
        {
            var workbook = await controlledImport.ExportProjectTemplateAsync(db, projectId, cancellationToken);
            return Results.File(
                workbook,
                "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                $"Project-{projectId}-BOM.xlsx");
        }
        catch (ControlledImportValidationException exception)
        {
            return Results.NotFound(new { message = exception.Message });
        }
    }

    private static async Task<IResult> ValidateProjectBomAsync(
        int projectId,
        IFormFile file,
        ProjectTrackerDbContext db,
        CurrentUserService currentUser,
        ControlledWorkbookImportService controlledImport,
        CancellationToken cancellationToken)
    {
        if (ValidateUploadedFile(file) is { } validationError)
            return validationError;

        try
        {
            await using var stream = new MemoryStream();
            await file.CopyToAsync(stream, cancellationToken);
            return Results.Ok(await controlledImport.ValidateProjectAsync(
                db,
                projectId,
                stream.ToArray(),
                file.FileName,
                currentUser.AccountName,
                cancellationToken));
        }
        catch (ControlledImportValidationException exception)
        {
            return Results.BadRequest(exception.Message);
        }
    }

    private static IResult DownloadProjectReviewAsync(
        int projectId,
        string reviewId,
        CurrentUserService currentUser,
        ControlledWorkbookImportService controlledImport)
    {
        try
        {
            var workbook = controlledImport.BuildProjectReviewWorkbook(
                projectId,
                reviewId,
                currentUser.AccountName);
            return Results.File(
                workbook,
                "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                $"Project-{projectId}-BOM-Review-{reviewId[..Math.Min(8, reviewId.Length)]}.xlsx");
        }
        catch (ControlledImportValidationException exception)
        {
            return Results.BadRequest(exception.Message);
        }
    }

    private static async Task<IResult> ConfirmProjectBomAsync(
        int projectId,
        string reviewId,
        ProjectTrackerDbContext db,
        CurrentUserService currentUser,
        ControlledWorkbookImportService controlledImport,
        CancellationToken cancellationToken)
    {
        try
        {
            return Results.Ok(await controlledImport.ApplyProjectAsync(
                db,
                projectId,
                reviewId,
                currentUser.AccountName,
                cancellationToken));
        }
        catch (ControlledImportConflictException exception)
        {
            return Results.Conflict(new { code = "ImportDataChanged", message = exception.Message });
        }
        catch (ControlledImportValidationException exception)
        {
            return Results.BadRequest(exception.Message);
        }
    }

    private static async Task<IResult> ValidateAsync(
        IFormFile file,
        ProjectTrackerDbContext db,
        CurrentUserService currentUser,
        ControlledWorkbookImportService controlledImport,
        CancellationToken cancellationToken)
    {
        if (ValidateUploadedFile(file) is { } validationError)
            return validationError;
        try
        {
            await using var stream = new MemoryStream();
            await file.CopyToAsync(stream, cancellationToken);
            return Results.Ok(await controlledImport.ValidateAsync(
                db,
                stream.ToArray(),
                file.FileName,
                currentUser.AccountName,
                cancellationToken));
        }
        catch (ControlledImportValidationException exception)
        {
            return Results.BadRequest(exception.Message);
        }
    }

    private static IResult? ValidateUploadedFile(IFormFile file)
    {
        if (file is null || file.Length == 0)
            return Results.BadRequest("Please choose a workbook file to upload.");

        var extension = Path.GetExtension(file.FileName);
        if (!string.Equals(extension, ".xlsx", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(extension, ".xlsm", StringComparison.OrdinalIgnoreCase))
            return Results.BadRequest("Upload a supported .xlsx or .xlsm Project Tracker workbook.");
        if (file.Length > 15 * 1024 * 1024)
            return Results.BadRequest("The workbook is larger than the 15 MB import limit.");
        return null;
    }

    private static async Task<IResult> ConfirmAsync(
        string reviewId,
        ProjectTrackerDbContext db,
        CurrentUserService currentUser,
        ControlledWorkbookImportService controlledImport,
        CancellationToken cancellationToken)
    {
        try
        {
            return Results.Ok(await controlledImport.ApplyAsync(
                db,
                reviewId,
                currentUser.AccountName,
                cancellationToken));
        }
        catch (ControlledImportConflictException exception)
        {
            return Results.Conflict(new { code = "ImportDataChanged", message = exception.Message });
        }
        catch (ControlledImportValidationException exception)
        {
            return Results.BadRequest(exception.Message);
        }
    }
}
