using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using ProjectTracker.Api.Auth;
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
            .RequireAuthorization(ProjectTrackerPagePolicies.ProjectDetail, AuthorizationPolicy);
        api.MapPost("/projects/{projectId:int}/quantities/sync/{provider}", SyncLegacyAsync)
            .RequireAuthorization(ProjectTrackerPagePolicies.ProjectDetail, AuthorizationPolicy);
        api.MapPost("/projects/{projectId:int}/routing/override", OverrideRoutingAsync)
            .RequireAuthorization(ProjectTrackerPagePolicies.ProjectDetail, RoutingOverrideAuthorizationPolicy);
        api.MapGet("/project-quantity-lookups/{kind}", SearchAsync)
            .RequireAuthorization(ProjectTrackerPagePolicies.ProjectDetail, AuthorizationPolicy);
        api.MapPost("/project-quantity-lookups/refresh", RefreshCatalogAsync)
            .RequireAuthorization(
                ProjectTrackerPagePolicies.ProjectDetail,
                RoutingOverrideAuthorizationPolicy);
        return api;
    }

    private static async Task<IResult> RefreshCatalogAsync(
        FulcrumProjectQuantityProvider provider,
        IEnterpriseProviderSource providerSource,
        CancellationToken cancellationToken)
    {
        var activeProvider = await providerSource.GetActiveProviderAsync(cancellationToken);
        if (!string.Equals(activeProvider, EnterpriseProviderNames.Fulcrum, StringComparison.OrdinalIgnoreCase))
            return Results.Conflict(new
            {
                detail = $"The active enterprise provider is {activeProvider}. Set Fulcrum as active before refreshing its lookup catalogue."
            });

        try
        {
            return Results.Ok(await provider.RefreshLookupCatalogAsync(cancellationToken));
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Results.Json(
                new { detail = "Fulcrum did not respond before the lookup catalogue refresh timed out. Try again." },
                statusCode: StatusCodes.Status504GatewayTimeout);
        }
        catch (Exception exception) when (exception is InvalidOperationException or HttpRequestException or JsonException or NotSupportedException)
        {
            return Results.Json(new { detail = exception.Message }, statusCode: StatusCodes.Status502BadGateway);
        }
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
        catch (Exception exception) when (exception is InvalidOperationException or HttpRequestException or JsonException or NotSupportedException)
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
        ILoggerFactory loggerFactory,
        ProjectQuantitySyncApplicationService syncApplication,
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
            loggerFactory,
            syncApplication,
            cancellationToken);

    private static Task<IResult> SyncLegacyAsync(
        int projectId,
        string provider,
        ProjectQuantitySyncRequestDto request,
        ProjectTrackerDbContext db,
        IEnumerable<IProjectQuantityProvider> providers,
        IEnterpriseProviderSource providerSource,
        ILoggerFactory loggerFactory,
        ProjectQuantitySyncApplicationService syncApplication,
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
            loggerFactory,
            syncApplication,
            cancellationToken);

    private static Task<IResult> OverrideRoutingAsync(
        int projectId,
        ProjectRoutingOverrideRequestDto request,
        ProjectTrackerDbContext db,
        IEnumerable<IProjectQuantityProvider> providers,
        IEnterpriseProviderSource providerSource,
        ILoggerFactory loggerFactory,
        ProjectQuantitySyncApplicationService syncApplication,
        CancellationToken cancellationToken) =>
        SyncAsync(
            projectId,
            null,
            request.Version,
            preserveQuantities: false,
            ProjectRoutingSyncMode.ForceOverride,
            db,
            providers,
            providerSource,
            loggerFactory,
            syncApplication,
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
        ILoggerFactory loggerFactory,
        ProjectQuantitySyncApplicationService syncApplication,
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
        catch (Exception exception) when (exception is InvalidOperationException or HttpRequestException or JsonException or NotSupportedException)
        {
            return Results.Json(
                new { detail = $"{quantityProvider.ProviderName} returned data that could not be used: {exception.Message}" },
                statusCode: StatusCodes.Status502BadGateway);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            loggerFactory
                .CreateLogger("ProjectQuantitySync")
                .LogError(exception, "Unexpected {Provider} project data response for project {ProjectId}.", quantityProvider.ProviderName, project.Id);
            return Results.Json(
                new { detail = $"{quantityProvider.ProviderName} could not complete the project data pull. No project data was changed. Try again or check the server log for the provider response." },
                statusCode: StatusCodes.Status502BadGateway);
        }

        if (!snapshot.MatchConfirmed)
            return Results.Ok(new ProjectQuantitySyncResultDto(
                ProjectDtoMapper.ToDetailDto(project),
                quantityProvider.ProviderName,
                [],
                [],
                snapshot.Warnings));

        try
        {
            return Results.Ok(await syncApplication.ApplyAsync(project, snapshot,
                quantityProvider.ProviderName, preserveQuantities, routingMode, cancellationToken));
        }
        catch (DbUpdateConcurrencyException)
        {
            return Results.Conflict(new ConcurrencyConflictDto(
                "ConcurrencyConflict",
                "This project changed while ERP data was being applied. Reload it and try again.",
                "Project",
                project.Id));
        }
        catch (DbUpdateException)
        {
            return Results.Conflict(new
            {
                detail = "The ERP data could not be applied because linked project records changed at the same time. Reload the project and try again."
            });
        }
    }
}
