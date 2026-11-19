using EstimatingDashboard.Api.Data;
using EstimatingDashboard.Api.Dtos;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace EstimatingDashboard.Api.Services;

internal sealed class QuoteTimingDiagnosticService(EstimatingAccessDbContext db,
    QuoteTimingDiagnosticClient client, IOptions<FulcrumQuoteSyncOptions> options)
{
    public async Task<QuoteTimingDiagnosticDto?> ReadAsync(int quoteHistoryId, CancellationToken ct)
    {
        var stored = await db.QuoteHistory.AsNoTracking().SingleOrDefaultAsync(row => row.Id == quoteHistoryId, ct);
        if (stored is null) return null;
        // SQLite cannot translate ordering a DateTimeOffset; sync timestamps are stored in UTC.
        var syncs = db.Database.IsSqlite()
            ? db.FulcrumQuoteSyncRuns.FromSqlRaw("SELECT * FROM \"FulcrumQuoteSyncRuns\" ORDER BY \"StartedAt\" DESC LIMIT 3")
            : db.FulcrumQuoteSyncRuns.OrderByDescending(run => run.StartedAt).Take(3);
        var recent = await syncs.AsNoTracking()
            .Select(run => new QuoteTimingSyncDto(run.Status, run.StartedAt, run.CompletedAt,
                run.QuotesReceived, run.NewRecords, run.UpdatedRecords, run.UnchangedRecords)).ToListAsync(ct);
        var mapping = options.Value.CustomFields;
        var reads = await client.ReadAsync(stored, ct);
        return new(stored.Id, stored.QuoteNumber,
            new(new(stored.DateToEstimating, stored.EstimatingCompletionDate, stored.RfqDueDate),
                stored.Workdays, stored.IsCompleted, stored.UpdatedAt),
            new(QuoteTimingDiagnosticProjection.BoundedName(mapping.DateToEstimating),
                QuoteTimingDiagnosticProjection.BoundedName(mapping.EstimatingCompletionDate),
                QuoteTimingDiagnosticProjection.BoundedName(mapping.RfqDueDate)), recent, reads.List, reads.Detail,
            new(reads.Definitions.Status, reads.Definitions.UpstreamStatusCode, reads.Definitions.RetryAt,
                reads.Definitions.Fields.Take(48).Select(field => new QuoteTimingDefinitionDto(
                    QuoteTimingDiagnosticProjection.BoundedName(field.Name), QuoteTimingDiagnosticProjection.BoundedName(field.Label))).ToArray(),
                reads.Definitions.Status == "Truncated" || reads.Definitions.Fields.Count > 48));
    }
}
