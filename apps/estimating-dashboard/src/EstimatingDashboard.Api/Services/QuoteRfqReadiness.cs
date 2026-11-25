using EstimatingDashboard.Api.Dtos;
using EstimatingDashboard.Api.Models;

namespace EstimatingDashboard.Api.Services;

internal static class QuoteRfqReadiness
{
    public static bool MaterialsSent(MaterialRequirementsDto inspection, MaterialPurchasingState? state,
        IReadOnlyList<MaterialDraftLink> links, out int requiredCount)
    {
        requiredCount = 0;
        if (inspection.PlanningSource != PurchasingPlanningSources.Fulcrum) return false;
        // Unconfirmed quantities or stale selections cannot prove coverage for every quoted tier,
        // even when the base line quantity looks stock-covered or has a recent purchase order.
        if (inspection.Warnings.Count > 0) return false;
        if (inspection.Materials.Any(material => material.Status == "review-required"
            || material.Status is not ("needs-rfq" or "recent-po" or "stock-covered"))) return false;
        var quantityFingerprint = MaterialDraftTracking.QuantitiesFingerprint(state);
        foreach (var material in inspection.Materials.Where(material => material.Status == "needs-rfq"))
        {
            requiredCount++;
            if (links.Count(link => link.Kind == "material" && link.IsCurrent
                    && link.ItemId == material.ItemId && link.SentAt.HasValue
                    && link.Fingerprint == QuoteMaterialPurchasingService.Fingerprint(material)
                    && link.QuantitiesFingerprint == quantityFingerprint) != 1)
                return false;
        }
        return true;
    }

    public static bool OutsideProcessingSent(OutsideProcessingRequirementsDto inspection,
        MaterialPurchasingState? state, IReadOnlyList<MaterialDraftLink> links, out int requiredCount)
    {
        requiredCount = 0;
        if (inspection.PlanningSource != PurchasingPlanningSources.Fulcrum) return false;
        if (inspection.Operations.Any(operation => operation.Status != "needs-rfq") || inspection.Warnings.Count > 0)
            return false;
        var quantityFingerprint = MaterialDraftTracking.QuantitiesFingerprint(state);
        foreach (var operation in inspection.Operations)
        {
            requiredCount++;
            if (links.Count(link => link.Kind == "op" && link.IsCurrent
                    && link.ItemId == operation.OperationKey && link.SentAt.HasValue
                    && link.Fingerprint == QuoteOutsideProcessingService.Fingerprint(operation)
                    && link.QuantitiesFingerprint == quantityFingerprint) != 1)
                return false;
        }
        return true;
    }
}
