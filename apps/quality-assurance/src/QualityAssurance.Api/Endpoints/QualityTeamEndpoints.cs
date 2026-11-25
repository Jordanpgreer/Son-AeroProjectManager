using System.Text.Json;
using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.AspNetCore.Mvc;
using QualityAssurance.Api.Auth;
using QualityAssurance.Api.Dtos;
using QualityAssurance.Api.Services;
using SonAero.Platform.Security;

namespace QualityAssurance.Api.Endpoints;

public static class QualityTeamEndpoints
{
    private static readonly JsonSerializerOptions TeamJsonOptions = new(JsonSerializerDefaults.Web);

    public static RouteGroupBuilder MapQualityTeamEndpoints(this RouteGroupBuilder api)
    {
        var teams = api.MapGroup("/admin/teams")
            .RequireAuthorization(QualityAssurancePermissions.SettingsView);

        teams.MapGet("/", async (
            QualityTeamService service,
            CancellationToken cancellationToken) =>
            Results.Ok(await service.GetAdminAsync(cancellationToken)));

        teams.MapPost("/", async (
            QualityTeamCreateDto dto,
            HttpContext context,
            QualityTeamService service,
            CancellationToken cancellationToken) =>
        {
            var created = await service.CreateAsync(dto, Access(context), cancellationToken);
            return Results.Created($"/api/admin/teams/{created.Id}", created);
        }).RequireAuthorization(QualityAssurancePermissions.TeamsManage);

        teams.MapPut("/{id:int}", async (
            int id,
            QualityTeamUpdateDto dto,
            HttpContext context,
            QualityTeamService service,
            CancellationToken cancellationToken) =>
        {
            var updated = await service.UpdateAsync(id, dto, Access(context), cancellationToken);
            return updated is null ? Results.NotFound() : Results.Ok(updated);
        }).RequireAuthorization(QualityAssurancePermissions.TeamsManage);

        teams.MapPost("/save/{id:int?}", SaveFromHubAsync)
            .WithMetadata(new RequestSizeLimitAttribute(32 * 1024))
            .RequireAuthorization(QualityAssurancePermissions.TeamsManage);

        teams.MapDelete("/{id:int}", async (
            int id,
            HttpContext context,
            QualityTeamService service,
            CancellationToken cancellationToken) =>
        {
            var disabled = await service.DisableAsync(id, Access(context), cancellationToken);
            return disabled is null ? Results.NotFound() : Results.Ok(disabled);
        }).RequireAuthorization(QualityAssurancePermissions.TeamsManage);

        return api;
    }

    public static async Task<IResult> SaveFromHubAsync(
        int? id,
        HttpContext context,
        QualityTeamService service,
        ICorsPolicyProvider corsPolicies,
        CancellationToken cancellationToken)
    {
        var request = context.Request;
        var allowedOrigins = (await corsPolicies.GetPolicyAsync(context, "HubAdmin"))?.Origins;
        if (!QualityRequestIntegrity.HasTrustedOrigin(request, allowedOrigins))
        {
            return Results.BadRequest(new ErrorDto(
                "UntrustedTeamRequest",
                "Save Quality teams from Arda's Admin Console."));
        }
        if (!QualityRequestIntegrity.HasMediaType(request, "text/plain"))
        {
            return Results.Json(new ErrorDto("InvalidTeamContentType", "The team request must use text/plain."),
                statusCode: StatusCodes.Status415UnsupportedMediaType);
        }

        QualityTeamUpdateDto? dto;
        try
        {
            dto = await JsonSerializer.DeserializeAsync<QualityTeamUpdateDto>(request.Body, TeamJsonOptions, cancellationToken);
        }
        catch (JsonException)
        {
            dto = null;
        }
        if (dto is null)
            return Results.BadRequest(new ErrorDto("InvalidTeamRequest", "Quality received invalid team details. Reopen the team editor and try again."));

        if (id.HasValue)
        {
            var updated = await service.UpdateAsync(id.Value, dto, Access(context), cancellationToken);
            return updated is null ? Results.NotFound() : Results.Ok(updated);
        }

        var created = await service.CreateAsync(
            new QualityTeamCreateDto(dto.Name, dto.Description, dto.MemberIds, dto.ManagerIds),
            Access(context),
            cancellationToken);
        return Results.Created($"/api/admin/teams/{created.Id}", created);
    }

    private static QualityAssuranceAccessProfile Access(HttpContext context) =>
        context.Items[QualityAssurancePolicies.AccessItem] as QualityAssuranceAccessProfile
        ?? throw new UnauthorizedAccessException("Quality Assurance access is unavailable.");
}
