using QualityAssurance.Api.Auth;
using QualityAssurance.Api.Dtos;
using QualityAssurance.Api.Services;
using SonAero.Platform.Security;

namespace QualityAssurance.Api.Endpoints;

public static class QualityWorkflowEndpoints
{
    public static RouteGroupBuilder MapQualityWorkflowEndpoints(this RouteGroupBuilder api)
    {
        var workflow = api.MapGroup("/admin/workflow").RequireAuthorization(QualityAssurancePermissions.SettingsView);
        workflow.MapGet("/", async (HttpContext context, QualityWorkflowService service, CancellationToken cancellationToken) =>
            Results.Ok(await service.GetAsync(Access(context), cancellationToken)));
        workflow.MapPut("/draft", async (QualityWorkflowSaveDto dto, HttpContext context,
            QualityWorkflowService service, CancellationToken cancellationToken) =>
            Results.Ok(await service.SaveAsync(dto, Access(context), cancellationToken)))
            .RequireAuthorization(QualityAssurancePermissions.RulesManage);
        workflow.MapPost("/publish", async (QualityWorkflowVersionDto dto, HttpContext context,
            QualityWorkflowService service, CancellationToken cancellationToken) =>
            Results.Ok(await service.PublishAsync(dto.Version, Access(context), cancellationToken)))
            .RequireAuthorization(QualityAssurancePermissions.RulesManage);
        workflow.MapPost("/simulate", async (QualityWorkflowSimulationDto dto, HttpContext context,
            QualityWorkflowService service, CancellationToken cancellationToken) =>
            Results.Ok(await service.SimulateAsync(dto, Access(context), cancellationToken)))
            .RequireAuthorization(QualityAssurancePermissions.RulesManage);
        workflow.MapGet("/options", async (
            IQualityAssuranceAccessStore accessStore,
            QualityTeamService teams,
            CancellationToken cancellationToken) =>
        {
            var options = await teams.GetAssignmentOptionsAsync(cancellationToken);
            var permissionGroups = await accessStore.GetGroupsAsync(cancellationToken);
            return Results.Ok(options with
            {
                PermissionGroups = permissionGroups.Select(group => new QualityDirectoryGroupDto(
                    group.Id,
                    group.Name,
                    group.Description,
                    group.ActiveUserCount)).ToList()
            });
        });
        return api;
    }

    private static QualityAssuranceAccessProfile Access(HttpContext context) =>
        context.Items[QualityAssurancePolicies.AccessItem] as QualityAssuranceAccessProfile
        ?? throw new UnauthorizedAccessException("Quality access is required.");
}
