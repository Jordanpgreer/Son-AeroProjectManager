using EstimatingDashboard.Api.Auth;
using EstimatingDashboard.Api.Dtos;
using EstimatingDashboard.Api.Models;
using EstimatingDashboard.Api.Services;
using Microsoft.AspNetCore.Http.Features;

namespace EstimatingDashboard.Api.Endpoints;

public static class VendorQuoteEndpoints
{
    public static RouteGroupBuilder MapVendorQuoteEndpoints(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/vendor-quotes").RequireAuthorization(EstimatingPolicies.ViewHistory);
        group.AddEndpointFilter(ErrorFilter);
        group.MapGet("", async (HttpContext ctx, VendorQuoteService service, string? search, string? status,
            int? quoteNumber, int page = 1, int pageSize = 50, CancellationToken cancellationToken = default) =>
            Results.Ok(await service.ListAsync(Access(ctx), search, status, quoteNumber, page, pageSize, cancellationToken)));
        group.MapGet("/options", async (HttpContext ctx, VendorQuoteService service, string? search, CancellationToken ct) =>
            Results.Ok(await service.OptionsAsync(Access(ctx), search, ct)));
        group.MapGet("/sync", async (HttpContext ctx, VendorQuoteService service, CancellationToken ct) =>
            Results.Ok(await service.SyncStatusAsync(Access(ctx), ct)));
        group.MapPost("/sync", async (HttpContext ctx, VendorQuoteService service, VendorQuoteSyncHeartbeatDto dto, CancellationToken ct) =>
            Results.Ok(await service.HeartbeatAsync(dto, Access(ctx), ct))).RequireAuthorization(EstimatingPolicies.Editor);
        group.MapGet("/{id:int}", async (int id, HttpContext ctx, VendorQuoteService service, CancellationToken ct) =>
            Results.Ok(await service.DetailAsync(id, Access(ctx), ct)));
        group.MapPost("", async (HttpContext ctx, VendorQuoteService service, CreateVendorQuoteDto dto, CancellationToken ct) =>
            Results.Ok(await service.CreateAsync(dto, Access(ctx), ct))).RequireAuthorization(EstimatingPolicies.Editor);
        group.MapPut("/{id:int}", async (int id, HttpContext ctx, VendorQuoteService service, UpdateVendorQuoteDto dto, CancellationToken ct) =>
            Results.Ok(await service.UpdateAsync(id, dto, Access(ctx), ct))).RequireAuthorization(EstimatingPolicies.Editor);
        group.MapPost("/{id:int}/notes", async (int id, HttpContext ctx, VendorQuoteService service, AddVendorQuoteNoteDto dto, CancellationToken ct) =>
            Results.Ok(await service.AddNoteAsync(id, dto, Access(ctx), ct))).RequireAuthorization(EstimatingPolicies.Editor);
        group.MapPost("/import", async (HttpContext ctx, VendorQuoteService service, CancellationToken ct) =>
        {
            // Set the bound before reading JSON: base64 encodes up to 20MB as ~28MB.
            var feature = ctx.Features.Get<IHttpMaxRequestBodySizeFeature>();
            if (feature is { IsReadOnly: false }) feature.MaxRequestBodySize = 30 * 1024 * 1024;
            if (ctx.Request.ContentLength > 30 * 1024 * 1024) return Results.StatusCode(413);
            if (!ctx.Request.HasJsonContentType()) return Results.StatusCode(415);
            ImportVendorQuoteMessageDto? dto;
            try { dto = await ctx.Request.ReadFromJsonAsync<ImportVendorQuoteMessageDto>(ct); }
            catch (System.Text.Json.JsonException) { return Results.BadRequest(new ErrorDto("InvalidEmail", "The import payload is not valid JSON.")); }
            return dto is null ? Results.BadRequest(new ErrorDto("InvalidEmail", "An email is required."))
                : Results.Ok(await service.ImportAsync(dto, Access(ctx), ct));
        }).RequireAuthorization(EstimatingPolicies.Editor);
        group.MapGet("/attachments/{id:long}", async (long id, HttpContext ctx, VendorQuoteService service, CancellationToken ct) =>
        {
            var file = await service.AttachmentAsync(id, Access(ctx), ct);
            ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
            ctx.Response.Headers.CacheControl = "private, no-store";
            return Results.File(file.Content, "application/octet-stream", file.FileName);
        });
        return api;
    }
    public static RouteGroupBuilder MapQuoteStatusEndpoints(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/quote-status").RequireAuthorization(EstimatingPolicies.ViewHistory);
        group.AddEndpointFilter(ErrorFilter);
        group.MapManualEmails();
        group.MapGet("", async (HttpContext ctx, QuoteStatusService service, string? search, string? status,
            int? quoteNumber, int page = 1, int pageSize = 50, CancellationToken cancellationToken = default) =>
            Results.Ok(await service.ListAsync(Access(ctx), search, status, quoteNumber, page, pageSize, cancellationToken)));
        group.MapGet("/options", (HttpContext ctx) =>
        {
            VendorQuoteService.Guard(Access(ctx));
            return Results.Ok(new QuoteStatusOptionsDto(EstimatingArdaStatuses.All, VendorQuoteStatuses.All));
        });
        group.MapGet("/{id:int}", async (int id, HttpContext ctx, QuoteStatusService service, CancellationToken ct) =>
            Results.Ok(await service.DetailAsync(id, Access(ctx), ct)));
        group.MapPut("/{id:int}", async (int id, HttpContext ctx, QuoteStatusService service, UpdateQuoteStatusDto dto, CancellationToken ct) =>
            Results.Ok(await service.UpdateAsync(id, dto, Access(ctx), ct))).RequireAuthorization(EstimatingPolicies.Editor);
        group.MapPost("/{id:int}/notes", async (int id, HttpContext ctx, QuoteStatusService service, AddQuoteStatusNoteDto dto, CancellationToken ct) =>
            Results.Ok(await service.AddNoteAsync(id, dto, Access(ctx), ct))).RequireAuthorization(EstimatingPolicies.Editor);
        group.MapPost("/{id:int}/messages/{messageId:long}/assign", async (int id, long messageId, HttpContext ctx, QuoteStatusService service, AssignQuoteMessageDto dto, CancellationToken ct) =>
            Results.Ok(await service.AssignMessageAsync(id, messageId, dto, Access(ctx), ct))).RequireAuthorization(EstimatingPolicies.Editor);
        return api;
    }
    private static EstimatingAccessProfile Access(HttpContext context) =>
        context.Items[EstimatingPolicies.AccessItem] as EstimatingAccessProfile
        ?? throw new VendorQuoteException(403, "Estimating access is required.");
    private static async ValueTask<object?> ErrorFilter(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        try { return await next(context); }
        catch (VendorQuoteException ex) { return Results.Json(new ErrorDto("QuoteStatusError", ex.Message), statusCode: ex.StatusCode); }
    }
}
