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
    public const string RoutingOverrideAuthorizationPolicy = "ProjectRoutingOverride";

    public static RouteGroupBuilder MapProjectQuantitySyncEndpoints(this RouteGroupBuilder api)
    {
        api.MapPost("/projects/{projectId:int}/quantities/sync", SyncActiveAsync)
            .RequireAuthorization(AuthorizationPolicy);
        api.MapPost("/projects/{projectId:int}/quantities/sync/{provider}", SyncLegacyAsync)
            .RequireAuthorization(AuthorizationPolicy);
        api.MapPost("/projects/{projectId:int}/routing/override", OverrideRoutingAsync)
            .RequireAuthorization(RoutingOverrideAuthorizationPolicy);
        api.MapGet("/project-quantity-lookups/{kind}", SearchAsync)
            .RequireAuthorization(AuthorizationPolicy);
        return api;
    }

    private static async Task<IResult> SearchAsync(
        string kind,
        string? query,
        string? partNumber,
        IEnumerable<IProjectQuantityProvider> providers,
        IEnterpriseProviderSource providerSource,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(query))
            return Results.BadRequest(new { detail = "Enter an item, sales order, or job search." });
        query = query.Trim();
        if (query.Length > 120)
            return Results.BadRequest(new { detail = "Search values cannot exceed 120 characters." });
        partNumber = string.IsNullOrWhiteSpace(partNumber) ? null : partNumber.Trim();
        if (partNumber?.Length > 120)
            return Results.BadRequest(new { detail = "Part numbers cannot exceed 120 characters." });

        var lookupKind = kind.Trim().ToLowerInvariant() switch
        {
            "item" => ProjectQuantityLookupKind.Item,
            "sales-order" => ProjectQuantityLookupKind.SalesOrder,
            "job" => ProjectQuantityLookupKind.Job,
            _ => (ProjectQuantityLookupKind?)null
        };
        if (lookupKind is null)
            return Results.BadRequest(new { detail = "Choose item, sales-order, or job lookup." });

        var activeProvider = await providerSource.GetActiveProviderAsync(cancellationToken);
        IProjectQuantityProvider provider;
        try
        {
            provider = EnterpriseAdapterSelector.Select(
                providers,
                activeProvider,
                EnterpriseDataRoutes.ProjectQuantities);
            var records = await provider.SearchAsync(
                lookupKind.Value,
                query,
                cancellationToken,
                lookupKind == ProjectQuantityLookupKind.SalesOrder ? partNumber : null);
            return Results.Ok(new
            {
                provider = provider.ProviderName,
                records
            });
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Results.Json(
                new { detail = $"{activeProvider} did not respond before the lookup timed out. Try again." },
                statusCode: StatusCodes.Status504GatewayTimeout);
        }
        catch (Exception exception) when (exception is InvalidOperationException or HttpRequestException)
        {
            return Results.Json(new { detail = exception.Message }, statusCode: StatusCodes.Status502BadGateway);
        }
    }

    private static Task<IResult> SyncActiveAsync(
        int projectId,
        ProjectQuantitySyncRequestDto request,
        ProjectTrackerDbContext db,
        IEnumerable<IProjectQuantityProvider> providers,
        IEnterpriseProviderSource providerSource,
        ProjectAuditService audit,
        ProjectRoutingSyncService routingSync,
        ProjectMetricsService metrics,
        CancellationToken cancellationToken) =>
        SyncAsync(
            projectId,
            null,
            request.Version,
            request.PreserveQuantities,
            ProjectRoutingSyncMode.PopulateWhenBlank,
            db,
            providers,
            providerSource,
            audit,
            routingSync,
            metrics,
            cancellationToken);

    private static Task<IResult> SyncLegacyAsync(
        int projectId,
        string provider,
        ProjectQuantitySyncRequestDto request,
        ProjectTrackerDbContext db,
        IEnumerable<IProjectQuantityProvider> providers,
        IEnterpriseProviderSource providerSource,
        ProjectAuditService audit,
        ProjectRoutingSyncService routingSync,
        ProjectMetricsService metrics,
        CancellationToken cancellationToken) =>
        SyncAsync(
            projectId,
            provider,
            request.Version,
            request.PreserveQuantities,
            ProjectRoutingSyncMode.PopulateWhenBlank,
            db,
            providers,
            providerSource,
            audit,
            routingSync,
            metrics,
            cancellationToken);

    private static Task<IResult> OverrideRoutingAsync(
        int projectId,
        ProjectRoutingOverrideRequestDto request,
        ProjectTrackerDbContext db,
        IEnumerable<IProjectQuantityProvider> providers,
        IEnterpriseProviderSource providerSource,
        ProjectAuditService audit,
        ProjectRoutingSyncService routingSync,
        ProjectMetricsService metrics,
        CancellationToken cancellationToken) =>
        SyncAsync(
            projectId,
            null,
            request.Version,
            preserveQuantities: true,
            ProjectRoutingSyncMode.ForceOverride,
            db,
            providers,
            providerSource,
            audit,
            routingSync,
            metrics,
            cancellationToken);

    private static async Task<IResult> SyncAsync(
        int projectId,
        string? requestedProvider,
        long requestedVersion,
        bool preserveQuantities,
        ProjectRoutingSyncMode routingMode,
        ProjectTrackerDbContext db,
        IEnumerable<IProjectQuantityProvider> providers,
        IEnterpriseProviderSource providerSource,
        ProjectAuditService audit,
        ProjectRoutingSyncService routingSync,
        ProjectMetricsService metrics,
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
        if (project.Version != requestedVersion)
            return Results.Conflict(new ConcurrencyConflictDto(
                "ConcurrencyConflict",
                "This project changed before the quantity pull started. Reload it and try again.",
                "Project",
                project.Id));
        var missingIdentifiers = new List<string>();
        if (string.IsNullOrWhiteSpace(project.ProgramName)) missingIdentifiers.Add("part number");
        if (string.IsNullOrWhiteSpace(project.SalesOrderNumber)) missingIdentifiers.Add("sales order number");
        if (string.IsNullOrWhiteSpace(project.JobNumber)) missingIdentifiers.Add("job number");
        if (missingIdentifiers.Count > 0)
            return Results.BadRequest(new
            {
                detail = $"Enter and save the {string.Join(", ", missingIdentifiers)} before pulling quantities. All three identifiers must match the same external record chain."
            });

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

        if (!snapshot.MatchConfirmed)
            return Results.Ok(new ProjectQuantitySyncResultDto(
                ProjectDtoMapper.ToDetailDto(project),
                quantityProvider.ProviderName,
                [],
                [],
                snapshot.Warnings));

        var before = ProjectAuditService.CaptureProject(project);
        var pulled = new List<string>();
        var retained = new List<string>();
        if (!preserveQuantities && snapshot.RequiredQuantity is not null)
        {
            project.RequiredQuantity = snapshot.RequiredQuantity;
            project.RequiredQuantitySource = quantityProvider.ProviderName;
            pulled.Add("Required quantity");
        }
        else if (project.RequiredQuantity is not null)
        {
            retained.Add("Required quantity");
        }

        if (!preserveQuantities && snapshot.JobQuantity is not null)
        {
            project.JobQuantity = snapshot.JobQuantity;
            project.JobQuantitySource = quantityProvider.ProviderName;
            pulled.Add("Job quantity");
        }
        else if (project.JobQuantity is not null)
        {
            retained.Add("Job quantity");
        }

        var syncTime = DateTimeOffset.UtcNow;
        var routingResult = routingSync.Apply(
            project,
            snapshot.ConfirmedRoutingSteps,
            quantityProvider.ProviderName,
            syncTime,
            routingMode);
        if (routingResult.RemovedTasks.Count > 0)
            db.Tasks.RemoveRange(routingResult.RemovedTasks);
        var warnings = snapshot.Warnings
            .Concat(routingResult.Warnings)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        project.QuantityLastSyncProvider = quantityProvider.ProviderName;
        project.QuantityLastSyncedAt = syncTime;
        project.UpdatedAt = project.QuantityLastSyncedAt.Value;
        project.Version++;
        if (routingResult.Added > 0
            || routingResult.Updated > 0
            || routingResult.ProgressUpdated > 0
            || routingResult.Removed > 0)
        {
            await metrics.RefreshProjectAsync(
                db,
                project,
                cancellationToken,
                preserveTaskSchedule: true);
        }
        var changes = ProjectAuditService.Diff(before, ProjectAuditService.CaptureProject(project)).ToList();
        if (routingResult.Added > 0)
            changes.Add(new ProjectAuditChange("Routing operations added", null, routingResult.Added.ToString()));
        if (routingResult.Updated > 0)
            changes.Add(new ProjectAuditChange("Routing operations updated", null, routingResult.Updated.ToString()));
        if (routingResult.ProgressUpdated > 0)
            changes.Add(new ProjectAuditChange("Fulcrum operation progress updated", null, routingResult.ProgressUpdated.ToString()));
        if (routingResult.ArdaOnlyRetained > 0)
            changes.Add(new ProjectAuditChange("Arda-only operations retained", null, routingResult.ArdaOnlyRetained.ToString()));
        if (routingResult.Removed > 0)
            changes.Add(new ProjectAuditChange("Operations removed by routing override", null, routingResult.Removed.ToString()));
        audit.Record(
            db,
            project,
            routingMode == ProjectRoutingSyncMode.ForceOverride ? "ProjectRoutingOverride" : "ProjectQuantitySync",
            routingMode == ProjectRoutingSyncMode.ForceOverride
                ? $"Overrode this project's operations from {quantityProvider.ProviderName}"
                : $"Refreshed project quantities from {quantityProvider.ProviderName}",
            changes);
        await db.SaveChangesAsync(cancellationToken);

        return Results.Ok(new ProjectQuantitySyncResultDto(
            ProjectDtoMapper.ToDetailDto(project),
            quantityProvider.ProviderName,
            pulled,
            retained,
            warnings,
            routingResult.Added,
            routingResult.Updated,
            routingResult.ArdaOnlyRetained,
            routingResult.Removed,
            routingResult.PreservedExisting,
            routingResult.ProgressUpdated));
    }
}
