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
        workflow.MapGet("/options", async (IQualityAssuranceAccessStore accessStore, CancellationToken cancellationToken) =>
        {
            var groups = await accessStore.GetGroupsWithPermissionAsync(QualityAssurancePermissions.ResponsibleGroupEligible, cancellationToken);
            var users = await accessStore.GetUsersWithPermissionAsync(QualityAssurancePermissions.AssignmentEligible, cancellationToken);
            return Results.Ok(new QualityAssignmentOptionsDto(
                groups.Select(group => new QualityDirectoryGroupDto(group.Id, group.Name, group.Description, group.ActiveUserCount)).ToList(),
                users.Select(user => new QualityDirectoryUserDto(user.Id, user.AccountName, user.DisplayName, user.GroupIds)).ToList()));
        });
        return api;
    }

    private static QualityAssuranceAccessProfile Access(HttpContext context) =>
        context.Items[QualityAssurancePolicies.AccessItem] as QualityAssuranceAccessProfile
        ?? throw new UnauthorizedAccessException("Quality access is required.");
}
