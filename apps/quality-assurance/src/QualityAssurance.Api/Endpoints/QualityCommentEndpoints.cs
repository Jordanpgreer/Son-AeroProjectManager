using QualityAssurance.Api.Auth;
using QualityAssurance.Api.Dtos;
using QualityAssurance.Api.Services;
using SonAero.Platform.Security;

namespace QualityAssurance.Api.Endpoints;

public static class QualityCommentEndpoints
{
    public static RouteGroupBuilder MapQualityCommentEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/shipments/{shipmentId:int}/comments", async (
            int shipmentId,
            long? afterId,
            HttpContext context,
            QualityShipmentCommentService comments,
            CancellationToken cancellationToken) =>
        {
            var thread = await comments.ListAsync(shipmentId, afterId, Access(context), cancellationToken);
            return thread is null ? Results.NotFound() : Results.Ok(thread);
        })
            .RequireAuthorization(QualityAssurancePermissions.ShipmentsView)
            .RequireAuthorization(QualityAssurancePermissions.CommentsView);

        api.MapPost("/shipments/{shipmentId:int}/comments", async (
            int shipmentId,
            QualityShipmentCommentCreateDto dto,
            HttpContext context,
            QualityShipmentCommentService comments,
            CancellationToken cancellationToken) =>
        {
            var created = await comments.PostAsync(shipmentId, dto, Access(context), cancellationToken);
            return created is null
                ? Results.NotFound()
                : Results.Created($"/api/shipments/{shipmentId}/comments/{created.Id}", created);
        })
            .RequireAuthorization(QualityAssurancePermissions.ShipmentsView)
            .RequireAuthorization(QualityAssurancePermissions.CommentsView)
            .RequireAuthorization(QualityAssurancePermissions.CommentsEdit);

        api.MapGet("/shipments/{shipmentId:int}/comment-mentions", async (
            int shipmentId,
            HttpContext context,
            QualityShipmentCommentService comments,
            CancellationToken cancellationToken) =>
        {
            var users = await comments.MentionableUsersAsync(
                shipmentId,
                Access(context),
                cancellationToken);
            return users is null ? Results.NotFound() : Results.Ok(users);
        })
            .RequireAuthorization(QualityAssurancePermissions.ShipmentsView)
            .RequireAuthorization(QualityAssurancePermissions.CommentsView);

        api.MapGet("/notifications", async (
            bool? unreadOnly,
            HttpContext context,
            QualityShipmentCommentService comments,
            CancellationToken cancellationToken) =>
            Results.Ok(await comments.NotificationsAsync(
                unreadOnly == true,
                Access(context),
                cancellationToken)))
            .RequireAuthorization(QualityAssurancePermissions.ShipmentsView)
            .RequireAuthorization(QualityAssurancePermissions.CommentsView);

        api.MapPost("/notifications/{notificationId:long}/read", MarkNotificationReadAsync)
            .RequireAuthorization(QualityAssurancePermissions.ShipmentsView)
            .RequireAuthorization(QualityAssurancePermissions.CommentsView);

        api.MapPost("/notifications/read-all", MarkAllNotificationsReadAsync)
            .RequireAuthorization(QualityAssurancePermissions.ShipmentsView)
            .RequireAuthorization(QualityAssurancePermissions.CommentsView);

        return api;
    }

    public static async Task<IResult> MarkNotificationReadAsync(
        long notificationId,
        HttpRequest request,
        HttpContext context,
        QualityShipmentCommentService comments,
        CancellationToken cancellationToken)
    {
        if (!QualityRequestIntegrity.IsTrustedAjaxRequest(request))
        {
            return Results.BadRequest(new ErrorDto(
                "UntrustedMutationRequest",
                "Notification changes must be submitted from the Quality Assurance application."));
        }

        return await comments.MarkNotificationReadAsync(
            notificationId,
            Access(context),
            cancellationToken)
            ? Results.NoContent()
            : Results.NotFound();
    }

    public static async Task<IResult> MarkAllNotificationsReadAsync(
        HttpRequest request,
        HttpContext context,
        QualityShipmentCommentService comments,
        CancellationToken cancellationToken)
    {
        if (!QualityRequestIntegrity.IsTrustedAjaxRequest(request))
        {
            return Results.BadRequest(new ErrorDto(
                "UntrustedMutationRequest",
                "Notification changes must be submitted from the Quality Assurance application."));
        }

        await comments.MarkAllNotificationsReadAsync(Access(context), cancellationToken);
        return Results.NoContent();
    }

    private static QualityAssuranceAccessProfile Access(HttpContext context) =>
        context.Items[QualityAssurancePolicies.AccessItem] as QualityAssuranceAccessProfile
        ?? throw new UnauthorizedAccessException("Quality Assurance access is unavailable.");
}
