using EstimatingDashboard.Api.Auth;
using EstimatingDashboard.Api.Dtos;
using EstimatingDashboard.Api.Services;

namespace EstimatingDashboard.Api.Endpoints;

public static class EstimatingQuoteWorkflowEndpoints
{
    public static RouteGroupBuilder MapEstimatingQuoteWorkflowEndpoints(this RouteGroupBuilder api)
    {
        var workflow = api.MapGroup("/quote-workflow")
            .RequireAuthorization(EstimatingPolicies.QuotesView);

        workflow.MapGet("/dashboard", DashboardAsync);
        workflow.MapGet("/performance", PerformanceAsync);

        workflow.MapGet("/mine", async (
            HttpContext context,
            EstimatingQuoteWorkflowService service,
            bool includeCompleted = false,
            CancellationToken cancellationToken = default) => Results.Ok(await service.GetMineAsync(
                Access(context),
                cancellationToken,
                includeCompleted)));

        workflow.MapGet("/stats", async (
            HttpContext context,
            EstimatingQuoteWorkflowService service,
            CancellationToken cancellationToken) => Results.Ok(await service.GetStatsAsync(
                Access(context),
                cancellationToken)));

        workflow.MapPost("/refresh", async (
            HttpContext context,
            EnterpriseQuoteSyncService sync,
            EstimatingQuoteWorkflowService workflowService,
            bool includeCompleted = false,
            CancellationToken cancellationToken = default) =>
        {
            try
            {
                var access = Access(context);
                await sync.RunPersonalAsync(access, cancellationToken);
                return Results.Ok(await workflowService.GetMineAsync(
                    access,
                    cancellationToken,
                    includeCompleted));
            }
            catch (EnterpriseQuoteSyncAlreadyRunningException exception)
            {
                return Results.Conflict(new ErrorDto("QuoteSyncAlreadyRunning", exception.Message));
            }
            catch (Exception exception) when (
                exception is HttpRequestException
                or InvalidOperationException
                or System.Text.Json.JsonException)
            {
                return Results.Problem(
                    statusCode: StatusCodes.Status502BadGateway,
                    title: "Fulcrum quote refresh failed",
                    detail: exception.Message);
            }
        }).RequireAuthorization(EstimatingPolicies.Editor);

        workflow.MapPut("/{quoteHistoryId:int}", async (
            int quoteHistoryId,
            UpdateEstimatingQuoteWorkflowDto request,
            HttpContext context,
            EstimatingQuoteWorkflowService service,
            CancellationToken cancellationToken) =>
        {
            try
            {
                return Results.Ok(await service.UpdateAsync(
                    quoteHistoryId,
                    request,
                    Access(context),
                    cancellationToken));
            }
            catch (EstimatingQuoteWorkflowNotFoundException)
            {
                return Results.NotFound(new ErrorDto(
                    "QuoteNotFound",
                    "The assigned quote was not found."));
            }
            catch (EstimatingQuoteWorkflowForbiddenException)
            {
                return Results.Json(
                    new ErrorDto(
                        "QuoteWorkflowForbidden",
                        "You can only update quotes assigned to you."),
                    statusCode: StatusCodes.Status403Forbidden);
            }
            catch (EstimatingQuoteWorkflowConflictException)
            {
                return Results.Conflict(new ErrorDto(
                    "QuoteWorkflowConflict",
                    "This quote changed after you opened it. Reload the quote and try again."));
            }
            catch (EstimatingQuoteWorkflowValidationException exception)
            {
                return Results.BadRequest(new ErrorDto(
                    "QuoteWorkflowValidation",
                    exception.Message));
            }
        }).RequireAuthorization(EstimatingPolicies.Editor);

        return api;
    }

    private static async Task<IResult> DashboardAsync(HttpContext context, EstimatingQuoteWorkflowService service,
        string? scope, string? from, string? to, string? search, string? status, string? estimator,
        int page = 1, int pageSize = 25, CancellationToken cancellationToken = default)
    {
        context.Response.Headers.CacheControl = "no-store";
        try
        {
            return Results.Ok(await service.GetDashboardAsync(Access(context), scope, from, to, search, status,
                page, pageSize, cancellationToken, estimator));
        }
        catch (EstimatingQuoteWorkflowForbiddenException)
        {
            return Results.Json(new ErrorDto("QuoteDashboardForbidden", "You do not have access to this quote scope."),
                statusCode: StatusCodes.Status403Forbidden);
        }
        catch (EstimatingQuoteWorkflowValidationException error)
        {
            return Results.BadRequest(new ErrorDto("QuoteDashboardValidation", error.Message));
        }
    }

    private static async Task<IResult> PerformanceAsync(HttpContext context, EstimatingQuoteWorkflowService service,
        string? scope, string? estimator, string? month, CancellationToken cancellationToken)
    {
        context.Response.Headers.CacheControl = "no-store";
        try
        {
            return Results.Ok(await service.GetPerformanceAsync(Access(context), scope, estimator, cancellationToken, month));
        }
        catch (EstimatingQuoteWorkflowForbiddenException)
        {
            return Results.Json(new ErrorDto("QuotePerformanceForbidden", "You do not have access to this performance view."),
                statusCode: StatusCodes.Status403Forbidden);
        }
        catch (EstimatingQuoteWorkflowValidationException error)
        {
            return Results.BadRequest(new ErrorDto("QuotePerformanceValidation", error.Message));
        }
    }

    private static EstimatingAccessProfile Access(HttpContext context) =>
        (context.Items[EstimatingPolicies.AccessItem] as EstimatingAccessProfile)
        ?? throw new InvalidOperationException("Estimating access was not resolved for this request.");
}
