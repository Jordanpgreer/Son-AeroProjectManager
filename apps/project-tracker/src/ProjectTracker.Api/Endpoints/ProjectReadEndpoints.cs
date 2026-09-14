using ProjectTracker.Api.Auth;
using ProjectTracker.Api.Services;
using SonAero.Platform.Security;

namespace ProjectTracker.Api.Endpoints;

public static class ProjectReadEndpoints
{
    public static RouteGroupBuilder MapProjectReadEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/dashboard", (CurrentUserService currentUser, ProjectReadService reads, CancellationToken cancellationToken) =>
            reads.DashboardAsync(
                currentUser.HasPermission(ApplicationPermissions.DashboardView)
                    || currentUser.HasPermission(ApplicationPermissions.ProjectDetailView),
                currentUser.HasPermission(ApplicationPermissions.PastProjectsView)
                    || currentUser.HasPermission(ApplicationPermissions.ProjectDetailView),
                cancellationToken))
            .RequireAuthorization(ProjectTrackerPagePolicies.AnyPage);

        api.MapGet("/projects", (ProjectReadService reads, CancellationToken cancellationToken) =>
            reads.SummariesAsync(cancellationToken))
            .RequireAuthorization(ProjectTrackerPagePolicies.ProjectDetail);

        api.MapGet("/projects/{id:int}", async (int id, ProjectReadService reads, CancellationToken cancellationToken) =>
        {
            var project = await reads.DetailAsync(id, cancellationToken);
            return project is null ? Results.NotFound() : Results.Ok(project);
        }).RequireAuthorization(ProjectTrackerPagePolicies.ProjectDetail);

        api.MapGet("/projects/{id:int}/version", async (int id, ProjectReadService reads, CancellationToken cancellationToken) =>
        {
            var project = await reads.VersionAsync(id, cancellationToken);
            return project is null ? Results.NotFound() : Results.Ok(project);
        }).RequireAuthorization(ProjectTrackerPagePolicies.ProjectDetail);

        api.MapGet("/preview", (ProjectReadService reads, CancellationToken cancellationToken) =>
            reads.PreviewAsync(cancellationToken))
            .RequireAuthorization(ProjectTrackerPagePolicies.Dashboard);

        api.MapGet("/calendar", (ProjectReadService reads, CancellationToken cancellationToken) =>
            reads.CalendarAsync(cancellationToken))
            .RequireAuthorization(ProjectTrackerPagePolicies.ScheduleData);

        return api;
    }
}
