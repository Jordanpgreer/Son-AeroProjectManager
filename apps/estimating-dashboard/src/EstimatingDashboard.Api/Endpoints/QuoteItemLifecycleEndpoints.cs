using EstimatingDashboard.Api.Auth;
using EstimatingDashboard.Api.Dtos;
using EstimatingDashboard.Api.Services;

namespace EstimatingDashboard.Api.Endpoints;

internal static class QuoteItemLifecycleEndpoints
{
    internal static void MapItemLifecycle(this RouteGroupBuilder group)
    {
        var routes = group.MapGroup("").RequireAuthorization(EstimatingPolicies.Editor);
        routes.MapPost("/{id:int}/messages/{messageId:long}/remove", async (int id, long messageId, QuoteItemVersionDto dto, HttpContext ctx, QuoteStatusService service, CancellationToken ct) =>
            Results.Ok(await service.RemoveEmailAsync(id, messageId, dto, Access(ctx), ct)))
            .RequireAuthorization(policy => policy.RequireClaim(EstimatingPolicies.PermissionClaim, EstimatingPermissions.DeleteQuotes));
        routes.MapPost("/{id:int}/messages/{messageId:long}/restore", async (int id, long messageId, QuoteItemVersionDto dto, HttpContext ctx, QuoteStatusService service, CancellationToken ct) =>
            Results.Ok(await service.RestoreEmailAsync(id, messageId, dto, Access(ctx), ct)))
            .RequireAuthorization(policy => policy.RequireClaim(EstimatingPolicies.PermissionClaim, EstimatingPermissions.DeleteQuotes));
        routes.MapPost("/{id:int}/messages/{messageId:long}/move", async (int id, long messageId, MoveQuoteEmailDto dto, HttpContext ctx, QuoteStatusService service, CancellationToken ct) =>
            Results.Ok(await service.MoveEmailAsync(id, messageId, dto, Access(ctx), ct)));
        routes.MapPut("/{id:int}/notes/{activityId}", async (int id, string activityId, EditQuoteNoteDto dto, HttpContext ctx, QuoteStatusService service, CancellationToken ct) =>
            Results.Ok(await service.EditNoteAsync(id, activityId, dto, Access(ctx), ct)));
        routes.MapPost("/{id:int}/notes/{activityId}/remove", async (int id, string activityId, QuoteItemVersionDto dto, HttpContext ctx, QuoteStatusService service, CancellationToken ct) =>
            Results.Ok(await service.RemoveNoteAsync(id, activityId, dto, Access(ctx), ct)))
            .RequireAuthorization(policy => policy.RequireClaim(EstimatingPolicies.PermissionClaim, EstimatingPermissions.DeleteQuotes));
        routes.MapPost("/{id:int}/notes/{activityId}/restore", async (int id, string activityId, QuoteItemVersionDto dto, HttpContext ctx, QuoteStatusService service, CancellationToken ct) =>
            Results.Ok(await service.RestoreNoteAsync(id, activityId, dto, Access(ctx), ct)))
            .RequireAuthorization(policy => policy.RequireClaim(EstimatingPolicies.PermissionClaim, EstimatingPermissions.DeleteQuotes));
    }
    private static EstimatingAccessProfile Access(HttpContext ctx) => ctx.Items[EstimatingPolicies.AccessItem] as EstimatingAccessProfile
        ?? throw new VendorQuoteException(403, "Estimating access is required.");
}
