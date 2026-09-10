using EstimatingDashboard.Api.Auth;
using EstimatingDashboard.Api.Dtos;
using EstimatingDashboard.Api.Services;
using Microsoft.AspNetCore.Http.Features;

namespace EstimatingDashboard.Api.Endpoints;

internal static class ManualQuoteEmailEndpoints
{
    internal static void MapManualEmails(this RouteGroupBuilder group)
    {
        group.MapPost("/{id:int}/emails/preview", async (int id, HttpContext ctx, VendorQuoteService service, CancellationToken ct) =>
        {
            var access = Access(ctx);
            await service.QuoteAsync(id, access, true, ct);
            var (_, file, content) = await Upload(ctx, ct);
            return Results.Ok(await service.PreviewEmailAsync(id, file.FileName, content, access, ct));
        }).RequireAuthorization(EstimatingPolicies.Editor);
        group.MapPost("/{id:int}/emails/import", async (int id, HttpContext ctx, VendorQuoteService service, QuoteStatusService quotes, CancellationToken ct) =>
        {
            var access = Access(ctx);
            await service.QuoteAsync(id, access, true, ct);
            var (form, file, content) = await Upload(ctx, ct);
            if (!int.TryParse(form["expectedVersion"], out var version) || version < 0) throw new VendorQuoteException(400, "The current quote version is required.");
            int? requestId = null;
            if (!string.IsNullOrWhiteSpace(form["requestId"]))
                requestId = int.TryParse(form["requestId"], out var parsed) && parsed > 0 ? parsed : throw new VendorQuoteException(400, "Choose a valid thread.");
            var result = await service.ImportEmailAsync(id, file.FileName, content,
                new(version, requestId, form["direction"].ToString(), form["vendorEmail"], form["note"]), access, ct);
            return Results.Ok(new ManualQuoteEmailImportResultDto(result.Outcome, result.RequestId, result.QuoteNumber!.Value,
                result.Message, await quotes.DetailAsync(id, access, ct)));
        }).RequireAuthorization(EstimatingPolicies.Editor);
    }
    private static EstimatingAccessProfile Access(HttpContext ctx) => ctx.Items[EstimatingPolicies.AccessItem] as EstimatingAccessProfile
        ?? throw new VendorQuoteException(403, "Estimating access is required.");
    internal static async Task<(IFormCollection, IFormFile, byte[])> Upload(HttpContext ctx, CancellationToken ct)
    {
        if (ctx.Request.Headers["X-Arda-Request"] != "vendor-quotes")
            throw new VendorQuoteException(403, "Open the email importer in Arda to upload this file.");
        const long bodyLimit = QuoteEmailFileParser.MaximumFileBytes + 64 * 1024;
        var feature = ctx.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (feature is { IsReadOnly: false }) feature.MaxRequestBodySize = bodyLimit;
        if (ctx.Request.ContentLength > bodyLimit) throw new VendorQuoteException(413, "Choose an email file up to 30 MB.");
        if (!ctx.Request.HasFormContentType) throw new VendorQuoteException(415, "Upload the email as multipart form data.");
        IFormCollection form;
        try { form = await ctx.Request.ReadFormAsync(new FormOptions { MultipartBodyLengthLimit = bodyLimit, ValueLengthLimit = 5000 }, ct); }
        catch (InvalidDataException) { throw new VendorQuoteException(400, "The email upload is too large or malformed."); }
        if (form.Files.Count != 1 || form.Files[0].Name != "file") throw new VendorQuoteException(400, "Choose one email file.");
        var file = form.Files[0];
        if (file.Length == 0 || file.Length > QuoteEmailFileParser.MaximumFileBytes) throw new VendorQuoteException(400, "Choose an email file up to 30 MB.");
        using var data = new MemoryStream(); await file.CopyToAsync(data, ct);
        return (form, file, data.ToArray());
    }
}
