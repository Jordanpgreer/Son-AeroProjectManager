using Microsoft.EntityFrameworkCore;
using ProjectTracker.Api.Data;
using ProjectTracker.Api.Dtos;
using ProjectTracker.Api.Mapping;
using ProjectTracker.Api.Models;
using ProjectTracker.Api.Services;
using SonAero.Platform.Integrations;

namespace ProjectTracker.Api.Endpoints;

public static class ProjectQuantitySyncEndpoints
{
    public const string AuthorizationPolicy = "ProjectQuantities";

    public static RouteGroupBuilder MapProjectQuantitySyncEndpoints(this RouteGroupBuilder api)
    {
        api.MapPost("/projects/{projectId:int}/quantities/sync", SyncActiveAsync)
            .RequireAuthorization(AuthorizationPolicy);
        api.MapPost("/projects/{projectId:int}/quantities/sync/{provider}", SyncLegacyAsync)
            .RequireAuthorization(AuthorizationPolicy);
        return api;
    }

    private static Task<IResult> SyncActiveAsync(
        int projectId,
        ProjectQuantitySyncRequestDto request,
        ProjectTrackerDbContext db,
        IEnumerable<IProjectQuantityProvider> providers,
        IEnterpriseProviderSource providerSource,
        ProjectAuditService audit,
        CancellationToken cancellationToken) =>
        SyncAsync(
            projectId,
            null,
            request,
            db,
            providers,
            providerSource,
            audit,
            cancellationToken);

    private static Task<IResult> SyncLegacyAsync(
        int projectId,
        string provider,
        ProjectQuantitySyncRequestDto request,
        ProjectTrackerDbContext db,
        IEnumerable<IProjectQuantityProvider> providers,
        IEnterpriseProviderSource providerSource,
        ProjectAuditService audit,
        CancellationToken cancellationToken) =>
        SyncAsync(
            projectId,
            provider,
            request,
            db,
            providers,
            providerSource,
            audit,
            cancellationToken);

    private static async Task<IResult> SyncAsync(
        int projectId,
        string? requestedProvider,
        ProjectQuantitySyncRequestDto request,
        ProjectTrackerDbContext db,
        IEnumerable<IProjectQuantityProvider> providers,
        IEnterpriseProviderSource providerSource,
        ProjectAuditService audit,
        CancellationToken cancellationToken)
    {
        var activeProvider = await providerSource.GetActiveProviderAsync(cancellationToken);
        if (requestedProvider is not null
            && !string.Equals(
                EnterpriseProviderNames.Normalize(requestedProvider),
                activeProvider,
                StringComparison.OrdinalIgnoreCase))
            return Results.Conflict(new
            {
                detail = $"The active enterprise provider is {activeProvider}. Reload the page and run the quantity pull again."
            });

        IProjectQuantityProvider quantityProvider;
        try
        {
            quantityProvider = EnterpriseAdapterSelector.Select(
                providers,
                activeProvider,
                EnterpriseDataRoutes.ProjectQuantities);
        }
        catch (InvalidOperationException exception)
        {
            return Results.Json(new { detail = exception.Message }, statusCode: StatusCodes.Status502BadGateway);
        }

        var project = await db.Projects
            .Include(candidate => candidate.Tasks)
                .ThenInclude(task => task.OvertimeDays)
            .SingleOrDefaultAsync(candidate => candidate.Id == projectId, cancellationToken);
        if (project is null) return Results.NotFound();
        if (project.Status == ProjectStatus.Complete)
            return Results.Conflict(new { detail = "Completed projects are read-only. Make the project active before syncing quantities." });
        if (project.Version != request.Version)
            return Results.Conflict(new ConcurrencyConflictDto(
                "ConcurrencyConflict",
                "This project changed before the quantity pull started. Reload it and try again.",
                "Project",
                project.Id));

        ProjectQuantitySnapshot snapshot;
        try
        {
            snapshot = await quantityProvider.PullAsync(project, cancellationToken);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Results.Json(
                new { detail = $"{quantityProvider.ProviderName} did not respond before the quantity pull timed out. Try again." },
                statusCode: StatusCodes.Status504GatewayTimeout);
        }
        catch (Exception exception) when (exception is InvalidOperationException or HttpRequestException)
        {
            return Results.Json(new { detail = exception.Message }, statusCode: StatusCodes.Status502BadGateway);
        }

        var before = ProjectAuditService.CaptureProject(project);
        var pulled = new List<string>();
        var retained = new List<string>();
        if (snapshot.RequiredQuantity is not null)
        {
            project.RequiredQuantity = snapshot.RequiredQuantity;
            project.RequiredQuantitySource = quantityProvider.ProviderName;
            pulled.Add("Required quantity");
        }
        else if (project.RequiredQuantity is not null)
        {
            retained.Add("Required quantity");
        }

        if (snapshot.JobQuantity is not null)
        {
            project.JobQuantity = snapshot.JobQuantity;
            project.JobQuantitySource = quantityProvider.ProviderName;
            pulled.Add("Job quantity");
        }
        else if (project.JobQuantity is not null)
        {
            retained.Add("Job quantity");
        }

        project.QuantityLastSyncProvider = quantityProvider.ProviderName;
        project.QuantityLastSyncedAt = DateTimeOffset.UtcNow;
        project.UpdatedAt = project.QuantityLastSyncedAt.Value;
        project.Version++;
        var changes = ProjectAuditService.Diff(before, ProjectAuditService.CaptureProject(project));
        audit.Record(
            db,
            project,
            "ProjectQuantitySync",
            $"Pulled project quantities from {quantityProvider.ProviderName}",
            changes);
        await db.SaveChangesAsync(cancellationToken);

        return Results.Ok(new ProjectQuantitySyncResultDto(
            ProjectDtoMapper.ToDetailDto(project),
            quantityProvider.ProviderName,
            pulled,
            retained,
            snapshot.Warnings));
    }
}
