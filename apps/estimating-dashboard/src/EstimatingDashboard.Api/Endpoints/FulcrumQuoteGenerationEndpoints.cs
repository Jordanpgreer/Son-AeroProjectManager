using EstimatingDashboard.Api.Auth;
using EstimatingDashboard.Api.Dtos;
using EstimatingDashboard.Api.Services;

namespace EstimatingDashboard.Api.Endpoints;

public static class FulcrumQuoteGenerationEndpoints
{
    public static RouteGroupBuilder MapFulcrumQuoteGenerationEndpoints(this RouteGroupBuilder api)
    {
        api.MapPost("/quote-workflow/{quoteHistoryId:int}/generate", async (int quoteHistoryId,
            HttpContext context, FulcrumQuoteGenerationService service,
            ILogger<FulcrumQuoteGenerationService> logger, CancellationToken cancellationToken) =>
        {
            try
            {
                if (!HasArdaRequestHeader(context.Request))
                    return Results.Json(new ErrorDto("InvalidRequest", "Quote generation requires the Arda application request header."), statusCode: 400);
                var access = context.Items[EstimatingPolicies.AccessItem] as EstimatingAccessProfile
                    ?? throw new InvalidOperationException("Estimating access was not resolved.");
                return Results.Ok(await service.GenerateAsync(quoteHistoryId, access, cancellationToken));
            }
            catch (EstimatingQuoteWorkflowNotFoundException)
            { return Results.NotFound(new ErrorDto("QuoteNotFound", "The assigned quote was not found.")); }
            catch (EstimatingQuoteWorkflowForbiddenException)
            { return Results.Json(new ErrorDto("QuoteForbidden", "You can only generate estimates for active quotes currently assigned to you."), statusCode: 403); }
            catch (FulcrumQuoteGenerationAlreadyRunningException)
            { return Results.Conflict(new ErrorDto("QuoteGenerationBusy", "Another quote is being generated. Wait for it to finish, then try again.")); }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            { return Results.Problem(statusCode: 504, title: "Fulcrum quote generation timed out", detail: "No partial estimate was created. Try again when Fulcrum is available."); }
            catch (Exception exception) when (exception is HttpRequestException or InvalidOperationException or System.Text.Json.JsonException)
            {
                logger.LogWarning(exception, "Fulcrum quote generation failed for history record {QuoteHistoryId}.", quoteHistoryId);
                return Results.Problem(statusCode: 502, title: "Could not generate quote",
                    detail: "Fulcrum data could not be safely converted into an estimate. Review the quote and API permissions, then try again.");
            }
        }).RequireAuthorization(EstimatingPolicies.ViewHistory, EstimatingPolicies.ManageInputs, EstimatingPolicies.Editor);
        return api;
    }

    internal static bool HasArdaRequestHeader(HttpRequest request) =>
        string.Equals(request.Headers["X-Arda-Request"], "quote-generation", StringComparison.Ordinal);
}
