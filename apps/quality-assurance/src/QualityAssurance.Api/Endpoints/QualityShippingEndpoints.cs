using Microsoft.AspNetCore.Mvc;
using QualityAssurance.Api.Auth;
using QualityAssurance.Api.Dtos;
using QualityAssurance.Api.Services;
using SonAero.Platform.Security;

namespace QualityAssurance.Api.Endpoints;

public static class QualityShippingEndpoints
{
    public const long MaxWorkbookBytes = 25L * 1024 * 1024;

    public static RouteGroupBuilder MapQualityShippingEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/dashboard", async (
            string? queue,
            int? page,
            int? pageSize,
            int? teamId,
            int? userId,
            HttpContext context,
            QualityShipmentService shipments,
            CancellationToken cancellationToken) =>
            Results.Ok(await shipments.DashboardAsync(
                Access(context), queue, page, pageSize, teamId, userId, cancellationToken)))
            .RequireAuthorization(QualityAssurancePermissions.DashboardView);

        api.MapGet("/dashboard/report", async (
            HttpContext context,
            QualityShipmentService shipments,
            CancellationToken cancellationToken) =>
        {
            var access = Access(context);
            var dashboard = await shipments.DashboardAsync(
                access, "open", 1, 100, null, null, cancellationToken);
            var bytes = new QualityDashboardReportService().Generate(
                dashboard,
                access.DisplayName,
                DateTimeOffset.UtcNow);
            return Results.File(
                bytes,
                "application/pdf",
                $"arda-quality-team-performance-{DateOnly.FromDateTime(DateTime.UtcNow):yyyy-MM-dd}.pdf");
        }).RequireAuthorization(QualityAssurancePermissions.DashboardView);

        api.MapGet("/shipments", async (
            string? status,
            string? scope,
            string? sort,
            string? direction,
            string? search,
            string? shipmentStatus,
            string[]? customer,
            string? assignee,
            HttpContext context,
            QualityShipmentService shipments,
            CancellationToken cancellationToken) =>
            Results.Ok(await shipments.ListAsync(
                Access(context), status, scope, sort, direction, search,
                shipmentStatus, customer, assignee, cancellationToken)))
            .RequireAuthorization(QualityAssurancePermissions.ShipmentsView);

        api.MapGet("/shipments/customer-options", async (
            string? status,
            string? scope,
            HttpContext context,
            QualityShipmentService shipments,
            CancellationToken cancellationToken) =>
            Results.Ok(await shipments.CustomerOptionsAsync(
                Access(context), status, scope, cancellationToken)))
            .RequireAuthorization(QualityAssurancePermissions.ShipmentsView);

        api.MapPost("/shipments/lookup", async (
            QualityShipmentLookupRequestDto dto,
            HttpContext context,
            IQualityShipmentSyncService synchronization,
            CancellationToken cancellationToken) =>
        {
            var access = Access(context);
            var shipperNumber = NormalizeShipperNumber(dto.ShipperNumber);
            var external = await synchronization.LookupAsync(shipperNumber, cancellationToken);
            if (external is null)
                return Results.NotFound(new ErrorDto(
                    "FulcrumShipmentNotFound",
                    $"No Fulcrum shipment matched {shipperNumber}. Check the shipper number and try again."));
            return Results.Ok(new QualityShipmentLookupDto(
                external.ShipperNumber,
                external.Status,
                external.ShipByDate,
                external.Customer,
                external.PurchaseOrderNumber,
                external.Parts.Select(part => new QualityShipmentPartInputDto(
                    part.PartNumber,
                    ToWholeQuantity(part.Quantity, part.PartNumber),
                    access.HasPermission(QualityAssurancePermissions.DollarValueEdit)
                        ? part.UnitPrice
                        : null,
                    null,
                    part.LineItemNumber)).ToList()));
        }).RequireAuthorization(
            QualityAssurancePermissions.ShipmentsView,
            QualityAssurancePermissions.ShipmentCreate);

        api.MapPost("/shipments/refresh", async (
            IQualityShipmentSyncService synchronization,
            CancellationToken cancellationToken) =>
            Results.Ok(new QualityShipmentRefreshResultDto(
                await synchronization.SyncAllAsync(cancellationToken))))
            .RequireAuthorization(QualityAssurancePermissions.ShipmentsView);

        api.MapGet("/shipments/{id:int}", async (
            int id,
            HttpContext context,
            QualityShipmentService shipments,
            CancellationToken cancellationToken) =>
        {
            var shipment = await shipments.GetAsync(id, Access(context), cancellationToken);
            return shipment is null ? Results.NotFound() : Results.Ok(shipment);
        }).RequireAuthorization(QualityAssurancePermissions.ShipmentsView);

        api.MapGet("/shipments/export", async (
            string? status,
            string? scope,
            string? sort,
            string? direction,
            string? search,
            string? shipmentStatus,
            string[]? customer,
            string? assignee,
            HttpContext context,
            QualityShipmentGridExportService exporter,
            CancellationToken cancellationToken) =>
        {
            var file = await exporter.CreateAsync(
                Access(context), status, scope, sort, direction, search,
                shipmentStatus, customer, assignee, cancellationToken);
            return Results.File(
                file.Content,
                "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                file.FileName);
        }).RequireAuthorization(QualityAssurancePermissions.ShipmentsView);

        api.MapGet("/shipping-layout", async (
            HttpContext context,
            QualityShippingLayoutService layouts,
            CancellationToken cancellationToken) =>
            Results.Ok(await layouts.GetAsync(Access(context), cancellationToken)))
            .RequireAuthorization(QualityAssurancePermissions.ShipmentsView);

        api.MapPut("/shipping-layout", async (
            QualityShippingLayoutUpdateDto dto,
            HttpContext context,
            QualityShippingLayoutService layouts,
            CancellationToken cancellationToken) =>
            Results.Ok(await layouts.SaveAsync(dto, Access(context), cancellationToken)))
            .RequireAuthorization(QualityAssurancePermissions.ShipmentsView);

        api.MapDelete("/shipping-layout", async (
            HttpContext context,
            QualityShippingLayoutService layouts,
            CancellationToken cancellationToken) =>
            Results.Ok(await layouts.ResetAsync(Access(context), cancellationToken)))
            .RequireAuthorization(QualityAssurancePermissions.ShipmentsView);

        api.MapPost("/shipments", async (
            QualityShipmentCreateDto dto,
            HttpContext context,
            QualityShipmentService shipments,
            CancellationToken cancellationToken) =>
        {
            var created = await shipments.CreateAsync(dto, Access(context), cancellationToken);
            return Results.Created($"/api/shipments/{created.Id}", created);
        }).RequireAuthorization(QualityAssurancePermissions.ShipmentsView, QualityAssurancePermissions.ShipmentCreate);

        api.MapPost("/shipments/import", ImportAsync)
            .DisableAntiforgery()
            .WithMetadata(new RequestSizeLimitAttribute(MaxWorkbookBytes + 128 * 1024))
            .RequireAuthorization(QualityAssurancePermissions.ShipmentsView, QualityAssurancePermissions.ShipmentImport);

        api.MapPatch("/shipments/{id:int}", async (
            int id,
            QualityShipmentPatchDto dto,
            HttpContext context,
            QualityShipmentService shipments,
            CancellationToken cancellationToken) =>
        {
            var updated = await shipments.PatchAsync(id, dto, Access(context), cancellationToken);
            return updated is null ? Results.NotFound() : Results.Ok(updated);
        }).RequireAuthorization(QualityAssurancePermissions.ShipmentsView);

        api.MapPost("/shipments/{id:int}/assignment", async (
            int id,
            QualityShipmentAssignmentDto dto,
            HttpContext context,
            QualityShipmentService shipments,
            CancellationToken cancellationToken) =>
        {
            var updated = await shipments.AssignAsync(id, dto, Access(context), cancellationToken);
            return updated is null ? Results.NotFound() : Results.Ok(updated);
        }).RequireAuthorization(QualityAssurancePermissions.AssignmentView);

        api.MapPost("/shipments/{id:int}/shipped", async (
            int id,
            QualityShipmentVersionDto dto,
            HttpContext context,
            QualityShipmentService shipments,
            CancellationToken cancellationToken) =>
        {
            var updated = await shipments.MarkShippedAsync(id, dto.Version, Access(context), cancellationToken);
            return updated is null ? Results.NotFound() : Results.Ok(updated);
        }).RequireAuthorization(QualityAssurancePermissions.ShipmentsView, QualityAssurancePermissions.MarkShipped);

        api.MapPost("/shipments/{id:int}/qa-complete", async (
            int id,
            QualityShipmentVersionDto dto,
            HttpContext context,
            QualityShipmentService shipments,
            CancellationToken cancellationToken) =>
        {
            var updated = await shipments.MarkQaCompleteAsync(
                id,
                dto.Version,
                Access(context),
                cancellationToken);
            return updated is null ? Results.NotFound() : Results.Ok(updated);
        }).RequireAuthorization(QualityAssurancePermissions.ShipmentsView, QualityAssurancePermissions.MarkShipped);

        api.MapGet("/shipments/{id:int}/audit", async (
            int id,
            HttpContext context,
            QualityShipmentService shipments,
            CancellationToken cancellationToken) =>
        {
            var audit = await shipments.AuditAsync(id, Access(context), cancellationToken);
            return audit is null ? Results.NotFound() : Results.Ok(audit);
        }).RequireAuthorization(QualityAssurancePermissions.ShipmentsView, QualityAssurancePermissions.AuditView);

        api.MapGet("/assignment-options", async (
            QualityTeamService teams,
            CancellationToken cancellationToken) =>
            Results.Ok(await teams.GetAssignmentOptionsAsync(cancellationToken)))
            .RequireAuthorization(QualityAssurancePermissions.AssignmentView);

        var rules = api.MapGroup("/admin/assignment-rules")
            .RequireAuthorization(QualityAssurancePermissions.SettingsView);
        rules.MapGet("/", async (QualityAssignmentService service, CancellationToken cancellationToken) =>
            Results.Ok(await service.GetRulesAsync(cancellationToken)));
        rules.MapGet("/options", async (QualityTeamService teams, CancellationToken cancellationToken) =>
            Results.Ok(await teams.GetAssignmentOptionsAsync(cancellationToken)));
        rules.MapPost("/", async (
            QualityAssignmentRuleUpsertDto dto,
            HttpContext context,
            QualityAssignmentService service,
            CancellationToken cancellationToken) =>
        {
            var created = await service.CreateRuleAsync(dto, Access(context), cancellationToken);
            return Results.Created($"/api/admin/assignment-rules/{created.Id}", created);
        }).RequireAuthorization(QualityAssurancePermissions.RulesManage);
        rules.MapPut("/{id:int}", async (
            int id,
            QualityAssignmentRuleUpsertDto dto,
            HttpContext context,
            QualityAssignmentService service,
            CancellationToken cancellationToken) =>
        {
            var updated = await service.UpdateRuleAsync(id, dto, Access(context), cancellationToken);
            return updated is null ? Results.NotFound() : Results.Ok(updated);
        }).RequireAuthorization(QualityAssurancePermissions.RulesManage);
        rules.MapDelete("/{id:int}", async (
            int id,
            long version,
            QualityAssignmentService service,
            CancellationToken cancellationToken) =>
            await service.DeleteRuleAsync(id, version, cancellationToken)
                ? Results.NoContent()
                : Results.NotFound())
            .RequireAuthorization(QualityAssurancePermissions.RulesManage);

        return api;
    }

    public static async Task<IResult> ImportAsync(
        HttpRequest request,
        IFormFile file,
        HttpContext context,
        QualityShipmentImportService importer,
        CancellationToken cancellationToken)
    {
        if (!QualityRequestIntegrity.IsTrustedMultipartAjaxRequest(request))
        {
            return Results.BadRequest(new ErrorDto(
                "UntrustedImportRequest",
                "Shipping Status imports must be submitted from the Quality Assurance application."));
        }
        if (file is null || file.Length == 0)
            return Results.BadRequest(new ErrorDto("EmptyWorkbook", "Choose a non-empty Excel workbook."));
        if (file.Length > MaxWorkbookBytes)
            return Results.BadRequest(new ErrorDto("WorkbookTooLarge", "The workbook cannot exceed 25 MB."));
        if (!string.Equals(Path.GetExtension(file.FileName), ".xlsx", StringComparison.OrdinalIgnoreCase))
            return Results.BadRequest(new ErrorDto("InvalidWorkbookType", "Upload an .xlsx Shipping Status workbook."));

        await using var stream = file.OpenReadStream();
        return Results.Ok(await importer.ImportAsync(
            stream,
            Path.GetFileName(file.FileName),
            Access(context),
            cancellationToken));
    }

    private static QualityAssuranceAccessProfile Access(HttpContext context) =>
        context.Items[QualityAssurancePolicies.AccessItem] as QualityAssuranceAccessProfile
        ?? throw new UnauthorizedAccessException("Quality Assurance access is unavailable.");

    private static string NormalizeShipperNumber(string? value)
    {
        var normalized = value?.Trim()
            ?? throw new ArgumentException("Shipper number is required.");
        if (normalized.Length == 0) throw new ArgumentException("Shipper number is required.");
        if (normalized.StartsWith("SHP-", StringComparison.OrdinalIgnoreCase)) return normalized;
        if (normalized.StartsWith("SO", StringComparison.OrdinalIgnoreCase)) return $"SHP-{normalized}";
        return $"SHP-SO{normalized}";
    }

    private static int? ToWholeQuantity(decimal? value, string partNumber)
    {
        if (!value.HasValue) return null;
        if (value.Value < 0 || decimal.Truncate(value.Value) != value.Value || value.Value > int.MaxValue)
            throw new InvalidOperationException(
                $"Fulcrum returned a non-whole or invalid quantity for part {partNumber}.");
        return decimal.ToInt32(value.Value);
    }

}
