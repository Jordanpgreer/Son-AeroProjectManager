using ProjectTracker.Api.Services;

namespace ProjectTracker.Api.Endpoints;

public static class ProjectReadEndpoints
{
    public static RouteGroupBuilder MapProjectReadEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/dashboard", (ProjectReadService reads, CancellationToken cancellationToken) =>
            reads.DashboardAsync(cancellationToken));

        api.MapGet("/projects", (ProjectReadService reads, CancellationToken cancellationToken) =>
            reads.SummariesAsync(cancellationToken));

        api.MapGet("/projects/{id:int}", async (int id, ProjectReadService reads, CancellationToken cancellationToken) =>
        {
            var project = await reads.DetailAsync(id, cancellationToken);
            return project is null ? Results.NotFound() : Results.Ok(project);
        });

        api.MapGet("/calendar", (ProjectReadService reads, CancellationToken cancellationToken) =>
            reads.CalendarAsync(cancellationToken));

        return api;
    }
}
