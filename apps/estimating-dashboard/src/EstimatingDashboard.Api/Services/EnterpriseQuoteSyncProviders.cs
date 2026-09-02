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
        return new EnterpriseQuotePullResult(mapping.Rows, snapshots.Count, mapping.Warnings);
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
