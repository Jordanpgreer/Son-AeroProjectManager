using EstimatingDashboard.Api.Auth;
using EstimatingDashboard.Api.Dtos;
using EstimatingDashboard.Api.Services;
using Microsoft.AspNetCore.Mvc;

namespace EstimatingDashboard.Api.Endpoints;

public static class FulcrumEstimateEndpoints
{
    public const long MaximumUploadBytes = FulcrumEstimateImportService.MaximumUploadBytes;

    public static RouteGroupBuilder MapFulcrumEstimateEndpoints(this RouteGroupBuilder api)
    {
        var estimates = api.MapGroup("/fulcrum-estimates")
            .RequireAuthorization(EstimatingPolicies.Viewer);

        estimates.MapPost("/preview", PreviewAsync)
            .DisableAntiforgery()
            .WithMetadata(new RequestSizeLimitAttribute(MaximumUploadBytes + 1024 * 1024))
            .WithMetadata(new RequestFormLimitsAttribute { MultipartBodyLengthLimit = MaximumUploadBytes })
            .RequireAuthorization(EstimatingPolicies.ManageInputs);

        estimates.MapPost("/{reviewId:guid}/export", Export)
            .WithMetadata(new RequestSizeLimitAttribute(256 * 1024))
            .RequireAuthorization(EstimatingPolicies.ManageInputs);

        estimates.MapGet("/rules", async (
            EstimatingOperationMappingService service,
            CancellationToken cancellationToken) =>
            Results.Ok(await service.GetCatalogAsync(cancellationToken)));

        estimates.MapPost("/rules", (
            HttpContext context,
            CreateEstimatingOperationMappingDto request,
            EstimatingOperationMappingService service,
            CancellationToken cancellationToken) =>
            MappingResult(async () =>
            {
                var created = await service.CreateAsync(request, Actor(context), cancellationToken);
                return Results.Created($"/api/fulcrum-estimates/rules/{created.Id}", created);
            }))
            .RequireAuthorization(EstimatingPolicies.AdministerRates);

        estimates.MapPut("/rules/{id:int}", (
            HttpContext context,
            int id,
            UpdateEstimatingOperationMappingDto request,
            EstimatingOperationMappingService service,
            CancellationToken cancellationToken) =>
            MappingResult(async () => Results.Ok(await service.UpdateAsync(
                id, request, Actor(context), cancellationToken))))
            .RequireAuthorization(EstimatingPolicies.AdministerRates);

        estimates.MapPost("/rules/{id:int}/deactivate", (
            HttpContext context,
            int id,
            DeactivateEstimatingOperationMappingDto request,
            EstimatingOperationMappingService service,
            CancellationToken cancellationToken) =>
            MappingResult(async () => Results.Ok(await service.DeactivateAsync(
                id, request.Version, Actor(context), cancellationToken))))
            .RequireAuthorization(EstimatingPolicies.AdministerRates);

        return api;
    }

    private static async Task<IResult> PreviewAsync(
        HttpContext context,
        IFormFile? file,
        FulcrumEstimateImportService service,
        CancellationToken cancellationToken)
    {
        if (file is null || file.Length == 0)
            return Results.BadRequest(new ErrorDto("EmptyWorkbook", "Choose a non-empty Fulcrum workbook."));
        if (file.Length > MaximumUploadBytes)
            return Results.BadRequest(new ErrorDto("WorkbookTooLarge", "The workbook cannot exceed 25 MB."));
        if (!string.Equals(Path.GetExtension(file.FileName), ".xlsx", StringComparison.OrdinalIgnoreCase))
            return Results.BadRequest(new ErrorDto("InvalidWorkbookType", "Upload a Fulcrum .xlsx workbook."));
        try
        {
            var access = Access(context);
            await using var stream = file.OpenReadStream();
            return Results.Ok(await service.PreviewAsync(
                stream,
                file.FileName,
                access.AccountName,
                access.DisplayName,
                cancellationToken));
        }
        catch (FulcrumEstimateValidationException exception)
        {
            return Results.BadRequest(new ErrorDto("InvalidFulcrumWorkbook", exception.Message));
        }
    }

    private static IResult Export(
        HttpContext context,
        Guid reviewId,
        FulcrumEstimateExportDto request,
        FulcrumEstimateExportService service)
    {
        try
        {
            var result = service.Export(reviewId, request, Actor(context));
            return Results.File(
                result.Content,
                "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                result.FileName);
        }
        catch (FulcrumEstimateReviewNotFoundException exception)
        {
            return Results.NotFound(new ErrorDto("EstimateReviewNotFound", exception.Message));
        }
        catch (FulcrumEstimateManualValidationException exception)
        {
            return Results.BadRequest(new ErrorDto("EstimateManualValidation", exception.Message));
        }
        catch (FulcrumEstimateValidationException exception)
        {
            return Results.BadRequest(new ErrorDto("EstimateExportValidation", exception.Message));
        }
    }

    private static async Task<IResult> MappingResult(Func<Task<IResult>> action)
    {
        try
        {
            return await action();
        }
        catch (EstimatingOperationMappingValidationException exception)
        {
            return Results.BadRequest(new ErrorDto("OperationRuleValidation", exception.Message));
        }
        catch (EstimatingOperationMappingNotFoundException)
        {
            return Results.NotFound(new ErrorDto("OperationRuleNotFound", "The operation rule was not found."));
        }
        catch (EstimatingOperationMappingConflictException exception)
        {
            return Results.Conflict(new ErrorDto("OperationRuleConflict", exception.Message));
        }
    }

    private static string Actor(HttpContext context) => Access(context).AccountName;

    private static EstimatingAccessProfile Access(HttpContext context) =>
        (context.Items[EstimatingPolicies.AccessItem] as EstimatingAccessProfile)
        ?? throw new InvalidOperationException("Estimating access was not resolved for this request.");
}
