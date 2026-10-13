using EstimatingDashboard.Api.Auth;
using EstimatingDashboard.Api.Dtos;
using EstimatingDashboard.Api.Services;

namespace EstimatingDashboard.Api.Endpoints;

internal static class MaterialPurchasingEndpoints
{
    public static RouteGroupBuilder MapMaterialPurchasingEndpoints(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/quote-status/{id:int}/materials")
            .RequireAuthorization(EstimatingPolicies.QuoteStatusView);
        group.AddEndpointFilter(async (context, next) =>
        {
            context.HttpContext.Response.Headers.CacheControl = "private, no-store";
            if (!HasWriteHeader(context.HttpContext.Request))
                return Results.Json(new ErrorDto("InvalidMaterialRequest", "Open this action from Arda's Quote Status page."), statusCode: 403);
            try { return await next(context); }
            catch (VendorQuoteException ex)
            { return Results.Json(new ErrorDto(ex.Code, ex.Message), statusCode: ex.StatusCode); }
            catch (HttpRequestException)
            { return Results.Json(new ErrorDto("FulcrumUnavailable", "Fulcrum purchasing data could not be read. Check its connection and API permissions, then retry."), statusCode: 502); }
            catch (OperationCanceledException) when (!context.HttpContext.RequestAborted.IsCancellationRequested)
            { return Results.Json(new ErrorDto("FulcrumTimeout", "Fulcrum took too long to respond. Retry the material review."), statusCode: 504); }
            catch (System.Text.Json.JsonException)
            { return Results.Json(new ErrorDto("MaterialReviewRequired", "Fulcrum returned incomplete material data. Refresh before creating a draft."), statusCode: 422); }
            catch (InvalidOperationException ex)
            { return Results.Json(new ErrorDto("MaterialReviewRequired", ex.Message), statusCode: 422); }
        });
        group.MapGet("", async (int id, HttpContext ctx, QuoteMaterialPurchasingService service, CancellationToken ct) =>
            Results.Ok(await service.ReadAsync(id, Access(ctx), ct)));
        group.MapPut("/quantities", async (int id, HttpContext ctx, SaveMaterialQuantitiesDto dto,
            QuoteMaterialPurchasingService service, CancellationToken ct) =>
            Results.Ok(await service.SaveQuantitiesAsync(id, dto, Access(ctx), ct)))
            .RequireAuthorization(EstimatingPolicies.Editor);
        group.MapPost("/{itemId}/draft", async (int id, string itemId, HttpContext ctx,
            QuoteMaterialPurchasingService service, CancellationToken ct) =>
        {
            var file = await service.DraftAsync(id, itemId, Access(ctx), ct);
            ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
            return Results.File(file.Content, "message/rfc822", file.FileName);
        }).RequireAuthorization(EstimatingPolicies.Editor);
        return api;
    }

    private static EstimatingAccessProfile Access(HttpContext context) =>
        context.Items[EstimatingPolicies.AccessItem] as EstimatingAccessProfile
        ?? throw new VendorQuoteException(403, "Estimating access is required.");

    // Cross-site HTML forms cannot supply this header; CORS only permits the configured Hub origins.
    internal static bool HasWriteHeader(HttpRequest request) => HttpMethods.IsGet(request.Method)
        || request.Headers["X-Arda-Request"] == "vendor-quotes";
}
