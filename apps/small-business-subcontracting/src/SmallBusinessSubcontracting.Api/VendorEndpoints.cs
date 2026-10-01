using SonAero.Platform.Security;

namespace SmallBusinessSubcontracting.Api;

public static class VendorEndpoints
{
    public static RouteGroupBuilder MapVendorEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/me", (HttpContext context) =>
        {
            var access = context.Items[SubcontractingAuthorization.AccessItem] as SubcontractingAccess
                ?? throw new InvalidOperationException("Module access was not resolved.");
            return Results.Ok(new MeDto(access.AccountName, access.DisplayName, access.Role, access.Permissions));
        });

        api.MapGet("/vendors", async (
            string? query,
            VendorService service,
            CancellationToken cancellationToken) =>
            Results.Ok(await service.GetVendorsAsync(query, cancellationToken)))
            .RequireAuthorization(SmallBusinessSubcontractingPermissions.VendorsView);

        api.MapGet("/vendors/{id:int}", async (
            int id,
            VendorService service,
            CancellationToken cancellationToken) =>
        {
            var vendor = await service.GetVendorAsync(id, cancellationToken);
            return vendor is null ? Results.NotFound() : Results.Ok(vendor);
        }).RequireAuthorization(SmallBusinessSubcontractingPermissions.VendorsView);

        api.MapPost("/vendors/sync", async (
            HttpContext context,
            VendorService service,
            CancellationToken cancellationToken) =>
            Results.Ok(await service.SynchronizeAsync(Actor(context), cancellationToken)))
            .RequireAuthorization(SmallBusinessSubcontractingPermissions.FulcrumSync);

        api.MapPut("/vendors/{id:int}/compliance", async (
            int id,
            ComplianceUpdateDto update,
            HttpContext context,
            VendorService service,
            CancellationToken cancellationToken) =>
        {
            var vendor = await service.UpdateComplianceAsync(
                id,
                update,
                Actor(context),
                cancellationToken);
            return vendor is null ? Results.NotFound() : Results.Ok(vendor);
        }).RequireAuthorization(SmallBusinessSubcontractingPermissions.ComplianceManage);

        api.MapPost("/vendors/{id:int}/documents", async (
            int id,
            HttpContext context,
            VendorService service,
            CancellationToken cancellationToken) =>
        {
            if (!context.Request.HasFormContentType)
                return Results.BadRequest(new { detail = "Submit the document as multipart form data." });
            var form = await context.Request.ReadFormAsync(cancellationToken);
            var file = form.Files.GetFile("file");
            if (file is null)
                return Results.BadRequest(new { detail = "Choose a document to upload." });
            if (!DateOnly.TryParse(form["documentDate"], out var documentDate))
                return Results.BadRequest(new { detail = "A valid document date is required." });
            var vendor = await service.AddDocumentAsync(
                id,
                file,
                form["documentType"].ToString(),
                documentDate,
                form["notes"].ToString(),
                Actor(context),
                cancellationToken);
            return vendor is null ? Results.NotFound() : Results.Ok(vendor);
        }).DisableAntiforgery().RequireAuthorization(SmallBusinessSubcontractingPermissions.DocumentsManage);

        api.MapGet("/documents/{id:guid}/download", async (
            Guid id,
            VendorService service,
            CancellationToken cancellationToken) =>
        {
            var document = await service.GetDocumentAsync(id, cancellationToken);
            return document is null
                ? Results.NotFound()
                : Results.File(
                    service.OpenDocument(document),
                    document.ContentType,
                    document.OriginalFileName,
                    enableRangeProcessing: true);
        }).RequireAuthorization(SmallBusinessSubcontractingPermissions.VendorsView);

        api.MapGet("/dashboard", async (
            string? query,
            string? businessSize,
            DateOnly? certificationFrom,
            DateOnly? certificationTo,
            string? sortBy,
            string? sortDirection,
            VendorService service,
            CancellationToken cancellationToken) => Results.Ok(await service.GetDashboardAsync(
                query,
                businessSize,
                certificationFrom,
                certificationTo,
                sortBy,
                sortDirection,
                cancellationToken)))
            .RequireAuthorization(SmallBusinessSubcontractingPermissions.DashboardView);

        api.MapGet("/dashboard/export", async (
            string? query,
            string? businessSize,
            DateOnly? certificationFrom,
            DateOnly? certificationTo,
            string? sortBy,
            string? sortDirection,
            VendorService service,
            CancellationToken cancellationToken) =>
        {
            var workbook = await service.ExportDashboardAsync(
                query,
                businessSize,
                certificationFrom,
                certificationTo,
                sortBy,
                sortDirection,
                cancellationToken);
            return Results.File(
                workbook,
                "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                $"vendor-compliance-{DateTime.UtcNow:yyyy-MM-dd}.xlsx");
        }).RequireAuthorization(SmallBusinessSubcontractingPermissions.Export);

        return api;
    }

    private static string Actor(HttpContext context) =>
        (context.Items[SubcontractingAuthorization.AccessItem] as SubcontractingAccess)?.DisplayName
        ?? context.User.Identity?.Name
        ?? "Unknown user";
}
