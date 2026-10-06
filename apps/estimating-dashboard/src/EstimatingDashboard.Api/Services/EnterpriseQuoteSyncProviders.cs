using EstimatingDashboard.Api.Data;
using EstimatingDashboard.Api.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SonAero.Platform.Integrations;

namespace EstimatingDashboard.Api.Services;

internal sealed record EnterpriseQuotePullResult(
    IReadOnlyList<EstimatingHistoryImportRow> Rows,
    int RecordsReceived,
    IReadOnlyList<string> Warnings);

internal interface IEstimatingQuoteProvider : IEnterpriseIntegrationAdapter
{
    Task<EnterpriseQuotePullResult> PullAsync(CancellationToken cancellationToken);
}

internal sealed class FulcrumEstimatingQuoteProvider(
    EstimatingAccessDbContext db,
    FulcrumQuoteClient client,
    FulcrumQuoteInspectionService inspectionService,
    IOptions<FulcrumQuoteSyncOptions> options) : IEstimatingQuoteProvider
{
    public string ProviderName => EnterpriseProviderNames.Fulcrum;
    public string RouteName => EnterpriseDataRoutes.EstimatingQuotes;

    public async Task<EnterpriseQuotePullResult> PullAsync(CancellationToken cancellationToken)
    {
        var snapshots = await client.GetQuotesAsync(cancellationToken);
        var quoteNumbers = snapshots.Select(snapshot => snapshot.Quote.Number).Distinct().ToList();
        var existing = await db.QuoteHistory
            .AsNoTracking()
            .Where(record => quoteNumbers.Contains(record.QuoteNumber))
            .ToDictionaryAsync(record => record.QuoteNumber, cancellationToken);
        var mapping = FulcrumQuoteMapper.Map(snapshots, existing, options.Value);
        var rows = mapping.Rows.ToDictionary(row => row.QuoteNumber);
        var activeSnapshots = snapshots
            .Where(snapshot => rows.TryGetValue(snapshot.Quote.Number, out var row) && !row.IsCompleted
                && !FulcrumQuoteStatuses.IsPostEstimating(row.QuoteStatus))
            .ToList();
        var inspection = await inspectionService.InspectAsync(activeSnapshots, cancellationToken);
        foreach (var pair in inspection.Results)
        {
            var details = pair.Value;
            rows[pair.Key] = rows[pair.Key] with
            {
                FulcrumQuoteItemsJson = System.Text.Json.JsonSerializer.Serialize(details.Items),
                FulcrumOpWarningsJson = System.Text.Json.JsonSerializer.Serialize(details.OpOperations),
                FulcrumBuyItemCount = details.BuyItemCount,
                FulcrumMakeItemCount = details.MakeItemCount,
                FulcrumInspectionUpdatedAt = details.InspectedAt,
                UpdateFulcrumInspection = true
            };
        }
        return new EnterpriseQuotePullResult(
            rows.Values.OrderBy(row => row.RowNumber).ToList(),
            snapshots.Count,
            mapping.Warnings.Concat(inspection.Warnings).ToList());
    }
}

internal sealed class AcumaticaEstimatingQuoteProvider : IEstimatingQuoteProvider
{
    public string ProviderName => EnterpriseProviderNames.Acumatica;
    public string RouteName => EnterpriseDataRoutes.EstimatingQuotes;

    public Task<EnterpriseQuotePullResult> PullAsync(CancellationToken cancellationToken) =>
        throw new InvalidOperationException(
            "The Acumatica estimating-quote adapter is installed but not configured. Add the tenant endpoint, authentication, and quote field mappings before activating Acumatica.");
}
