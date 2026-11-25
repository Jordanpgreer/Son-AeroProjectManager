using EstimatingDashboard.Api.Auth;
using EstimatingDashboard.Api.Data;
using EstimatingDashboard.Api.Dtos;
using EstimatingDashboard.Api.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace EstimatingDashboard.Api.Services;

public interface IQuoteRfqProgression
{
    Task TryAdvanceAsync(EstimatingQuoteHistoryRecord quote, EstimatingAccessProfile access, CancellationToken ct);
}

// Advances only after a fresh Fulcrum inspection proves every actionable draft has sent evidence.
internal sealed class QuoteRfqProgressionService(EstimatingAccessDbContext db,
    IFulcrumMaterialRequirementsReader materials, IFulcrumOutsideProcessingReader outsideProcessing,
    TimeProvider clock, ILogger<QuoteRfqProgressionService>? logger = null) : IQuoteRfqProgression
{
    public async Task TryAdvanceAsync(EstimatingQuoteHistoryRecord quote, EstimatingAccessProfile access,
        CancellationToken ct)
    {
        var current = EstimatingArdaStatuses.Normalize(quote.ArdaStatus) ?? EstimatingArdaStatuses.Untouched;
        if (current is not (EstimatingArdaStatuses.Untouched or EstimatingArdaStatuses.InProgress)) return;
        try
        {
            var state = await db.Set<MaterialPurchasingState>().SingleOrDefaultAsync(row => row.QuoteHistoryId == quote.Id, ct);
            var overrides = MaterialQuantitySelections.ToOverrides(MaterialQuantitySelections.Read(state?.QuantitiesJson));
            var materialReview = await materials.InspectAsync(quote.Id, quote.QuoteNumber, quote.SourceId, ct, overrides);
            // Fulcrum cannot prove completion for work whose planning remains in Industrios or is unknown.
            if (materialReview.PlanningSource != PurchasingPlanningSources.Fulcrum) return;
            var opReview = await outsideProcessing.InspectAsync(quote.Id, quote.QuoteNumber, quote.SourceId, overrides, ct);
            var links = MaterialDraftTracking.Read(state);
            if (!QuoteRfqReadiness.MaterialsSent(materialReview, state, links, out var materialCount)
                || !QuoteRfqReadiness.OutsideProcessingSent(opReview, state, links, out var opCount)
                || materialCount + opCount == 0) return;
            var now = clock.GetUtcNow();
            quote.AuditHistory.Add(new EstimatingQuoteHistoryAuditRecord {
                QuoteHistory = quote, QuoteNumber = quote.QuoteNumber, ImportBatchId = Guid.Empty,
                Action = EstimatingQuoteAuditActions.WorkflowUpdated, FieldName = "Arda status",
                OldValue = current, NewValue = EstimatingArdaStatuses.RfqSent,
                ChangedAt = now, ChangedBy = access.AccountName });
            quote.ArdaStatus = EstimatingArdaStatuses.RfqSent;
            quote.ArdaStatusChangedAt = now;
            quote.ArdaStatusChangedBy = access.AccountName;
            quote.Version++;
            quote.UpdatedAt = now;
            quote.UpdatedBy = access.AccountName;
        }
        catch (Exception error) when (!ct.IsCancellationRequested)
        {
            // A failed or incomplete provider read cannot prove that all requests were sent.
            // The sent email is still imported; the next confirmation can retry progression.
            logger?.LogWarning(error, "RFQ Sent could not be verified for quote {QuoteNumber}", quote.QuoteNumber);
        }
    }
}
