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
        api.MapGet("/project-quantity-lookups/{kind}", SearchAsync)
            .RequireAuthorization(AuthorizationPolicy);
        return api;
    }

    private static async Task<IResult> SearchAsync(
        string kind,
        string? query,
        IEnumerable<IProjectQuantityProvider> providers,
        IEnterpriseProviderSource providerSource,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(query))
            return Results.BadRequest(new { detail = "Enter an exact sales order number, job number, or job name." });
        query = query.Trim();
        if (query.Length > 120)
            return Results.BadRequest(new { detail = "Search values cannot exceed 120 characters." });

        var lookupKind = kind.Trim().ToLowerInvariant() switch
        {
            "sales-order" => ProjectQuantityLookupKind.SalesOrder,
            "job" => ProjectQuantityLookupKind.Job,
            _ => (ProjectQuantityLookupKind?)null
        };
        if (lookupKind is null)
            return Results.BadRequest(new { detail = "Choose either sales-order or job lookup." });

        var activeProvider = await providerSource.GetActiveProviderAsync(cancellationToken);
        IProjectQuantityProvider provider;
        try
        {
            provider = EnterpriseAdapterSelector.Select(
                providers,
                activeProvider,
                EnterpriseDataRoutes.ProjectQuantities);
            var records = await provider.SearchAsync(lookupKind.Value, query, cancellationToken);
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
