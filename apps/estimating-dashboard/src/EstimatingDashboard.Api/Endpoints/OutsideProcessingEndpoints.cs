using EstimatingDashboard.Api.Auth;
using EstimatingDashboard.Api.Dtos;
using EstimatingDashboard.Api.Services;
using SonAero.Platform.Integrations;

namespace EstimatingDashboard.Api.Endpoints;

internal static class OutsideProcessingEndpoints
{
    public static RouteGroupBuilder MapOutsideProcessingEndpoints(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/quote-status/{id:int}/outside-processing")
            .RequireAuthorization(EstimatingPolicies.QuoteStatusView);
        group.AddEndpointFilter(ReviewFilter);
        group.MapGet("", async (int id, HttpContext ctx, QuoteOutsideProcessingService service, CancellationToken ct) =>
            Results.Ok(await service.ReadAsync(id, Access(ctx), ct)));
        group.MapPost("/{operationKey}/draft", async (int id, string operationKey, HttpContext ctx,
            QuoteOutsideProcessingService service, CancellationToken ct) =>
        {
            var draft = await service.DraftAsync(id, operationKey, Access(ctx), ct);
            ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
            return Results.File(draft.Content, "message/rfc822", draft.FileName);
        }).RequireAuthorization(EstimatingPolicies.Editor);
        return api;
    }

    internal static async ValueTask<object?> ReviewFilter(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        context.HttpContext.Response.Headers.CacheControl = "private, no-store";
        if (!HttpMethods.IsGet(context.HttpContext.Request.Method)
            && context.HttpContext.Request.Headers["X-Arda-Request"] != "vendor-quotes")
            return Results.Json(new ErrorDto("InvalidOpRequest", "Open this action from Arda's Quote Status page."), statusCode: 403);
        try { return await next(context); }
        catch (VendorQuoteException ex)
        { return Results.Json(new ErrorDto(ex.Code, ex.Message), statusCode: ex.StatusCode); }
        catch (FulcrumCoordinationUnavailableException)
        { return Results.Json(new ErrorDto("OpDataUnavailable", "Outside processing data is temporarily unavailable. Try again shortly."), statusCode: 503); }
        catch (HttpRequestException)
        { return Results.Json(new ErrorDto("FulcrumUnavailable", "Fulcrum outside processing data could not be read. Check its connection and API permissions."), statusCode: 502); }
        catch (OperationCanceledException) when (!context.HttpContext.RequestAborted.IsCancellationRequested)
        { return Results.Json(new ErrorDto("FulcrumTimeout", "Fulcrum took too long to inspect the routing. Retry."), statusCode: 504); }
        catch (System.Text.Json.JsonException)
        { return Results.Json(new ErrorDto("OpReviewRequired", "Fulcrum returned incomplete outside processing data. Refresh before creating a draft."), statusCode: 422); }
        catch (InvalidOperationException ex)
        { return Results.Json(new ErrorDto("OpReviewRequired", ex.Message), statusCode: 422); }
    }

    private static EstimatingAccessProfile Access(HttpContext context) =>
        context.Items[EstimatingPolicies.AccessItem] as EstimatingAccessProfile
        ?? throw new VendorQuoteException(403, "Estimating access is required.");
}
