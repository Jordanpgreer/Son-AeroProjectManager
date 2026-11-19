using System.Data.Common;
using EstimatingDashboard.Api.Auth;
using EstimatingDashboard.Api.Dtos;
using EstimatingDashboard.Api.Services;

namespace EstimatingDashboard.Api.Endpoints;

internal static class QuoteTimingDiagnosticEndpoints
{
    public static RouteGroupBuilder MapQuoteTimingDiagnosticEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/quote-history/{quoteHistoryId:int}/timing-diagnostics", async (
            int quoteHistoryId, HttpContext context, QuoteTimingDiagnosticService service, CancellationToken ct) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            try
            {
                var result = await service.ReadAsync(quoteHistoryId, ct);
                return result is null ? Results.NotFound(new ErrorDto("QuoteHistoryNotFound", "The quote record was not found."))
                    : Results.Ok(result);
            }
            catch (DbException)
            {
                ct.ThrowIfCancellationRequested();
                return Results.Json(new ErrorDto("TimingDiagnosticUnavailable", "Quote timing diagnostics are temporarily unavailable."), statusCode: 503);
            }
        }).RequireAuthorization(EstimatingPolicies.ViewHistory, EstimatingPolicies.Admin);
        return api;
    }
}
