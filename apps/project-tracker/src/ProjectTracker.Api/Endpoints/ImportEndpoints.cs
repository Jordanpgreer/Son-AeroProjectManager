using ProjectTracker.Api.Data;
using ProjectTracker.Api.Dtos;
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
        }).RequireAuthorization("AdminOnly");

        api.MapPost("/import/upload", UploadAsync)
            .RequireAuthorization("AdminOnly")
            .DisableAntiforgery();
        return api;
    }

    private static async Task<IResult> UploadAsync(
        IFormFile file,
        ProjectTrackerDbContext db,
        WorkbookImportService importer,
        CancellationToken cancellationToken)
    {
        if (file is null || file.Length == 0)
        {
            return Results.BadRequest("Please choose a workbook file to upload.");
        }

        var extension = Path.GetExtension(file.FileName);
        if (!string.Equals(extension, ".xlsx", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(extension, ".xlsm", StringComparison.OrdinalIgnoreCase))
        {
            return Results.BadRequest("Upload a .xlsx or .xlsm workbook.");
        }

        var tempPath = Path.Combine(Path.GetTempPath(), $"pt-upload-{Guid.NewGuid():N}{extension}");
        try
        {
            await using (var stream = File.Create(tempPath))
            {
                await file.CopyToAsync(stream, cancellationToken);
            }

            return Results.Ok(await importer.ImportAsync(db, tempPath, replaceExisting: false, cancellationToken));
        }
        finally
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);
        }
    }
}
