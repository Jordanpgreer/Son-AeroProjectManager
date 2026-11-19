using System.Text.Json;
using EstimatingDashboard.Api.Dtos;
using static EstimatingDashboard.Api.Services.FulcrumMaterialRequirementsPolicy;

namespace EstimatingDashboard.Api.Services;

internal sealed record FulcrumPurchasingPlan(bool IsIndustrios, IReadOnlyList<FulcrumQuoteRouting?> Routes);

internal static class FulcrumPurchasingPlanning
{
    public static string Source(int inspectedParts, int industriosParts) => inspectedParts == 0
        ? PurchasingPlanningSources.Unknown
        : industriosParts == inspectedParts ? PurchasingPlanningSources.Industrios
        : industriosParts == 0 ? PurchasingPlanningSources.Fulcrum : PurchasingPlanningSources.Mixed;

    public static async Task<FulcrumPurchasingPlan> InspectAsync(FulcrumQuoteGenerationClient client,
        string quoteId, string itemId, JsonElement item, IEnumerable<string> lineIds, CancellationToken ct)
    {
        // Purchased top-level parts still require procurement despite having no manufacturing operations.
        if (Text(item, "itemOrigin") is not ("make" or "makeOrBuy" or "kit")) return new(false, [null]);
        var routes = new List<FulcrumQuoteRouting?>();
        var hasQuotePlan = false;
        foreach (var lineId in lineIds)
        {
            if (string.IsNullOrWhiteSpace(lineId))
                throw new InvalidOperationException("A quoted part has no planning identifier. Its purchasing requirements could not be verified.");
            var route = await FulcrumQuoteRouting.LoadIfAvailableAsync(client, quoteId, lineId, itemId, ct);
            routes.Add(route);
            if (route is not null && (route.HasIncompleteSummary || route.HasRoutes && !route.Contains(itemId)))
                throw new InvalidOperationException("Fulcrum returned incomplete quote planning. Purchasing requirements could not be verified.");
            if (route?.Contains(itemId) != true) continue;
            var operations = await route.OperationsAsync(itemId, ct);
            var inputs = await route.InputsAsync(itemId, ct);
            hasQuotePlan |= operations.Count > 0 || inputs.Any(input => Usage(input) is not (0, 0));
        }
        // Quote-specific planning takes precedence over empty or obsolete item-catalog operations.
        if (hasQuotePlan) return new(false, routes);
        var catalog = await client.ListAsync(
            $"api/items/{FulcrumQuoteGenerationClient.Identifier(itemId)}/routing/operations/list", true, ct);
        if (catalog.Count > 0) return new(false, routes);
        // A missing endpoint/404 is not proof of an empty quote plan.
        if (routes.Count == 0 || routes.Any(route => route is null))
            throw new InvalidOperationException("Fulcrum quote planning is unavailable. Industrios planning could not be confirmed.");
        return new(true, routes);
    }
}
