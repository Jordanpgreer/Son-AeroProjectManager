using System.Net;
using Microsoft.EntityFrameworkCore;
using ProjectTracker.Api.Data;
using ProjectTracker.Api.Models;
using SonAero.Platform.Integrations;
using SonAero.Platform.Security;

namespace ProjectTracker.Api.Services;

/// <summary>Projects the shared Fulcrum snapshots into eligible active projects.</summary>
public sealed class ProjectQuantityScheduledSyncService(
    IServiceScopeFactory scopeFactory,
    ILogger<ProjectQuantityScheduledSyncService> logger)
{
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        List<int> projectIds;
        await using (var scope = scopeFactory.CreateAsyncScope())
        {
            var providerSource = scope.ServiceProvider.GetRequiredService<IEnterpriseProviderSource>();
            if (!string.Equals(await providerSource.GetActiveProviderAsync(cancellationToken),
                EnterpriseProviderNames.Fulcrum, StringComparison.OrdinalIgnoreCase)) return;
            var credentials = scope.ServiceProvider.GetRequiredService<IProjectTrackerIntegrationCredentialReader>();
            if (string.IsNullOrWhiteSpace(await credentials.GetSecretAsync(
                IntegrationCredentialNames.FulcrumPublicApi, cancellationToken))) return;
            var db = scope.ServiceProvider.GetRequiredService<ProjectTrackerDbContext>();
            projectIds = await db.Projects.AsNoTracking()
                .Where(project => project.Status != ProjectStatus.Complete
                    && project.ProgramName != "" && project.SalesOrderNumber != null
                    && project.SalesOrderNumber != "" && project.JobNumber != null && project.JobNumber != "")
                .OrderBy(project => project.Id).Select(project => project.Id).ToListAsync(cancellationToken);
        }

        var synced = 0;
        foreach (var projectId in projectIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // An isolated scope prevents one failed/concurrent project from contaminating the next.
            await using var scope = scopeFactory.CreateAsyncScope();
            try
            {
                var providerSource = scope.ServiceProvider.GetRequiredService<IEnterpriseProviderSource>();
                if (!string.Equals(await providerSource.GetActiveProviderAsync(cancellationToken),
                    EnterpriseProviderNames.Fulcrum, StringComparison.OrdinalIgnoreCase)) break;
                var db = scope.ServiceProvider.GetRequiredService<ProjectTrackerDbContext>();
                var project = await db.Projects.Include(candidate => candidate.Tasks)
                    .ThenInclude(task => task.OvertimeDays)
                    .SingleOrDefaultAsync(candidate => candidate.Id == projectId, cancellationToken);
                if (project is null || project.Status == ProjectStatus.Complete
                    || string.IsNullOrWhiteSpace(project.ProgramName)
                    || string.IsNullOrWhiteSpace(project.SalesOrderNumber)
                    || string.IsNullOrWhiteSpace(project.JobNumber)) continue;
                var provider = EnterpriseAdapterSelector.Select(
                    scope.ServiceProvider.GetServices<IProjectQuantityProvider>(),
                    EnterpriseProviderNames.Fulcrum, EnterpriseDataRoutes.ProjectQuantities);
                var snapshot = await provider.PullAsync(project, cancellationToken);
                if (!snapshot.MatchConfirmed) continue;
                var application = scope.ServiceProvider.GetRequiredService<ProjectQuantitySyncApplicationService>();
                await application.ApplyAsync(project, snapshot, provider.ProviderName,
                    preserveQuantities: false, ProjectRoutingSyncMode.PopulateWhenBlank,
                    cancellationToken, scheduled: true);
                synced++;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (HttpRequestException exception) when (exception.StatusCode is
                HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                or HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable)
            {
                logger.LogWarning(exception, "Scheduled ERP synchronization paused after a connection or authorization failure; remaining projects will retry next cycle.");
                break;
            }
            catch (DbUpdateConcurrencyException)
            {
                logger.LogInformation("Project {ProjectId} changed during scheduled ERP synchronization; retrying next cycle.", projectId);
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Scheduled ERP synchronization failed for project {ProjectId}; other projects will continue.", projectId);
            }
        }
        logger.LogInformation("Scheduled ERP synchronization updated {Synced} of {Eligible} eligible projects.", synced, projectIds.Count);
    }
}
