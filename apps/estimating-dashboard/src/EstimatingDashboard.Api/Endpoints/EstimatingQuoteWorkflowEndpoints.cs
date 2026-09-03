using EstimatingDashboard.Api.Auth;
using EstimatingDashboard.Api.Dtos;
using EstimatingDashboard.Api.Services;

namespace EstimatingDashboard.Api.Endpoints;

public static class EstimatingQuoteWorkflowEndpoints
{
    public static RouteGroupBuilder MapEstimatingQuoteWorkflowEndpoints(this RouteGroupBuilder api)
    {
        var workflow = api.MapGroup("/quote-workflow")
            .RequireAuthorization(EstimatingPolicies.ViewHistory);

        workflow.MapGet("/mine", async (
            HttpContext context,
            EstimatingQuoteWorkflowService service,
            CancellationToken cancellationToken) => Results.Ok(await service.GetMineAsync(
                Access(context),
                cancellationToken)));

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

    private static EstimatingAccessProfile Access(HttpContext context) =>
        (context.Items[EstimatingPolicies.AccessItem] as EstimatingAccessProfile)
        ?? throw new InvalidOperationException("Estimating access was not resolved for this request.");
}
