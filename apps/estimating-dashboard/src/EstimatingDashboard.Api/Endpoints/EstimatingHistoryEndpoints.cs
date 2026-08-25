using EstimatingDashboard.Api.Auth;
using EstimatingDashboard.Api.Dtos;
using EstimatingDashboard.Api.Services;

namespace EstimatingDashboard.Api.Endpoints;

public static class EstimatingHistoryEndpoints
{
    public static RouteGroupBuilder MapEstimatingHistoryEndpoints(this RouteGroupBuilder api)
    {
        var history = api.MapGroup("/quote-history")
            .RequireAuthorization(EstimatingPolicies.ViewHistory);

        history.MapGet("", async (
            EstimatingHistoryQueryService service,
            string? search,
            string? estimator,
            string? salesPerson,
            string? customer,
            string? quoteStatus,
            string? estimatingStatus,
            string? complexity,
            string? issues,
            string? quoteOnTrack,
            string? view,
            string? completion,
            string? onTime,
            DateTime? dueFrom,
            DateTime? dueTo,
            DateTime? completedFrom,
            DateTime? completedTo,
            decimal? minimumValue,
            decimal? maximumValue,
            string? sort,
            string? direction,
            int page = 1,
            int pageSize = 50,
            CancellationToken cancellationToken = default) => Results.Ok(await service.GetPageAsync(
                search,
                estimator,
                salesPerson,
                customer,
                quoteStatus,
                estimatingStatus,
                complexity,
                issues,
                quoteOnTrack,
                view,
                completion,
                onTime,
                dueFrom,
                dueTo,
                completedFrom,
                completedTo,
                minimumValue,
                maximumValue,
                sort,
                direction,
                page,
                pageSize,
                cancellationToken)));

        history.MapGet("/filters", async (
            EstimatingHistoryQueryService service,
            CancellationToken cancellationToken) => Results.Ok(await service.GetFiltersAsync(cancellationToken)));

        history.MapGet("/dashboard", async (
            HttpContext context,
            EstimatingHistoryQueryService service,
            string? period,
            CancellationToken cancellationToken) => Results.Ok(await service.GetDashboardAsync(
                period,
                Access(context),
                cancellationToken)));

        history.MapGet("/report", async (
            string? period,
            string? estimator,
            EstimatingHistoryReportService service,
            CancellationToken cancellationToken) =>
        {
            if (!EstimatingHistoryPeriods.IsValidReportPeriod(period))
                return Results.BadRequest(new ErrorDto(
                    "InvalidReportPeriod",
                    "Choose week, month, or year for the statistics report."));
            var report = await service.CreateAsync(period!, estimator, cancellationToken);
            return Results.File(
                report.Content,
                "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                report.FileName);
        }).RequireAuthorization(EstimatingPolicies.ManageHistory);

        history.MapGet("/{quoteHistoryId:int}/audit", async (
            int quoteHistoryId,
            EstimatingHistoryQueryService service,
            CancellationToken cancellationToken) =>
        {
            var result = await service.GetAuditHistoryAsync(quoteHistoryId, cancellationToken);
            return result is null
                ? Results.NotFound(new ErrorDto("QuoteHistoryNotFound", "The quote record was not found."))
                : Results.Ok(result);
        }).RequireAuthorization(EstimatingPolicies.ManageHistory);

        history.MapPost("/import/validate", async (
            HttpContext context,
            IFormFile file,
            EstimatingHistoryImportService service,
            CancellationToken cancellationToken) =>
        {
            if (file.Length == 0)
                return Results.BadRequest(new ErrorDto("EmptyWorkbook", "Choose a non-empty Excel workbook."));
            if (file.Length > 25 * 1024 * 1024)
                return Results.BadRequest(new ErrorDto("WorkbookTooLarge", "The workbook cannot exceed 25 MB."));
            if (!string.Equals(Path.GetExtension(file.FileName), ".xlsx", StringComparison.OrdinalIgnoreCase))
                return Results.BadRequest(new ErrorDto("InvalidWorkbookType", "Upload an .xlsx Fulcrum export or Daily Quote Log workbook."));

            var actor = Actor(context);
            await using var stream = file.OpenReadStream();
            var result = await service.ValidateAsync(stream, file.FileName, actor, cancellationToken);
            return Results.Ok(result);
        }).DisableAntiforgery().RequireAuthorization(EstimatingPolicies.ImportHistory);

        history.MapPost("/import/{reviewId:guid}/apply", async (
            HttpContext context,
            Guid reviewId,
            EstimatingHistoryImportApplyDto request,
            EstimatingHistoryImportService service,
            CancellationToken cancellationToken) =>
        {
            try
            {
                return Results.Ok(await service.ApplyAsync(
                    reviewId,
                    Actor(context),
                    request.ContinueWithErrors,
                    cancellationToken));
            }
            catch (EstimatingHistoryReviewNotFoundException exception)
            {
                return Results.NotFound(new ErrorDto("ImportReviewNotFound", exception.Message));
            }
            catch (EstimatingHistoryImportValidationException exception)
            {
                return Results.BadRequest(new ErrorDto("ImportValidation", exception.Message));
            }
            catch (EstimatingHistoryImportConflictException exception)
            {
                return Results.Conflict(new ErrorDto("ImportConflict", exception.Message));
            }
        }).RequireAuthorization(EstimatingPolicies.ImportHistory);

        return api;
    }

    private static string Actor(HttpContext context) =>
        (context.Items[EstimatingPolicies.AccessItem] as EstimatingAccessProfile)?.AccountName
        ?? context.User.Identity?.Name
        ?? "Unknown user";

    private static EstimatingAccessProfile Access(HttpContext context) =>
        (context.Items[EstimatingPolicies.AccessItem] as EstimatingAccessProfile)
        ?? throw new InvalidOperationException("Estimating access was not resolved for this request.");
}
