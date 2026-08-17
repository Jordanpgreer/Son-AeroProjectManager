using QualityAssurance.Api.Auth;
using QualityAssurance.Api.Dtos;
using QualityAssurance.Api.Services;
using SonAero.Platform.Security;

namespace QualityAssurance.Api.Endpoints;

public static class QualityShippingEndpoints
{
    public static RouteGroupBuilder MapQualityShippingEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/dashboard", async (
            HttpContext context,
            QualityShipmentService shipments,
            CancellationToken cancellationToken) =>
            Results.Ok(await shipments.DashboardAsync(Access(context), cancellationToken)))
            .RequireAuthorization(QualityAssurancePermissions.ShipmentsView);

        api.MapGet("/shipments", async (
            string? status,
            string? scope,
            string? sort,
            string? search,
            HttpContext context,
            QualityShipmentService shipments,
            CancellationToken cancellationToken) =>
            Results.Ok(await shipments.ListAsync(
                Access(context), status, scope, sort, search, cancellationToken)))
            .RequireAuthorization(QualityAssurancePermissions.ShipmentsView);

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
        }).RequireAuthorization(QualityAssurancePermissions.ShipmentCreate);

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
        }).RequireAuthorization(QualityAssurancePermissions.MarkShipped);

        api.MapGet("/shipments/{id:int}/audit", async (
            int id,
            HttpContext context,
            QualityShipmentService shipments,
            CancellationToken cancellationToken) =>
        {
            var audit = await shipments.AuditAsync(id, Access(context), cancellationToken);
            return audit is null ? Results.NotFound() : Results.Ok(audit);
        }).RequireAuthorization(QualityAssurancePermissions.AuditView);

        api.MapGet("/assignment-options", async (
            IQualityAssuranceAccessStore accessStore,
            CancellationToken cancellationToken) =>
            Results.Ok(await OptionsAsync(accessStore, cancellationToken)))
            .RequireAuthorization(QualityAssurancePermissions.AssignmentView);

        var rules = api.MapGroup("/admin/assignment-rules")
            .RequireAuthorization(QualityAssurancePermissions.RulesManage);
        rules.MapGet("/", async (QualityAssignmentService service, CancellationToken cancellationToken) =>
            Results.Ok(await service.GetRulesAsync(cancellationToken)));
        rules.MapGet("/options", async (IQualityAssuranceAccessStore accessStore, CancellationToken cancellationToken) =>
            Results.Ok(await OptionsAsync(accessStore, cancellationToken)));
        rules.MapPost("/", async (
            QualityAssignmentRuleUpsertDto dto,
            HttpContext context,
            QualityAssignmentService service,
            CancellationToken cancellationToken) =>
        {
            var created = await service.CreateRuleAsync(dto, Access(context), cancellationToken);
            return Results.Created($"/api/admin/assignment-rules/{created.Id}", created);
        });
        rules.MapPut("/{id:int}", async (
            int id,
            QualityAssignmentRuleUpsertDto dto,
            HttpContext context,
            QualityAssignmentService service,
            CancellationToken cancellationToken) =>
        {
            var updated = await service.UpdateRuleAsync(id, dto, Access(context), cancellationToken);
            return updated is null ? Results.NotFound() : Results.Ok(updated);
        });
        rules.MapDelete("/{id:int}", async (
            int id,
            long version,
            QualityAssignmentService service,
            CancellationToken cancellationToken) =>
            await service.DeleteRuleAsync(id, version, cancellationToken)
                ? Results.NoContent()
                : Results.NotFound());

        return api;
    }

    private static QualityAssuranceAccessProfile Access(HttpContext context) =>
        context.Items[QualityAssurancePolicies.AccessItem] as QualityAssuranceAccessProfile
        ?? throw new UnauthorizedAccessException("Quality Assurance access is unavailable.");

    private static async Task<QualityAssignmentOptionsDto> OptionsAsync(
        IQualityAssuranceAccessStore accessStore,
        CancellationToken cancellationToken)
    {
        var groups = await accessStore.GetGroupsAsync(cancellationToken);
        var users = await accessStore.GetUsersAsync(null, cancellationToken);
        return new QualityAssignmentOptionsDto(
            groups.Select(group => new QualityDirectoryGroupDto(
                group.Id, group.Name, group.Description, group.ActiveUserCount)).ToList(),
            users.Select(user => new QualityDirectoryUserDto(
                user.Id, user.AccountName, user.DisplayName, user.GroupIds)).ToList());
    }
}
