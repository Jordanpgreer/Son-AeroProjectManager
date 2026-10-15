using QualityAssurance.Api.Auth;
using QualityAssurance.Api.Dtos;
using QualityAssurance.Api.Services;
using SonAero.Platform.Security;

namespace QualityAssurance.Api.Endpoints;

public static class QualityTeamEndpoints
{
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

    private static QualityAssuranceAccessProfile Access(HttpContext context) =>
        context.Items[QualityAssurancePolicies.AccessItem] as QualityAssuranceAccessProfile
        ?? throw new UnauthorizedAccessException("Quality Assurance access is unavailable.");
}
