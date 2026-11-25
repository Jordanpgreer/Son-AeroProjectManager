using System.Text.Json;
using static EstimatingDashboard.Api.Services.FulcrumMaterialRequirementsPolicy;

namespace EstimatingDashboard.Api.Services;

internal static class FulcrumQuoteQuantityTiers
{
    public const int MaximumCount = 200;

    public static (decimal[] Quantities, bool Complete) Read(IEnumerable<JsonElement> lines, IEnumerable<decimal> lineQuantities)
    {
        var values = lineQuantities.ToHashSet();
        var complete = true;
        foreach (var line in lines)
        {
            // Missing break data cannot establish that the base line quantity is the only tier.
            if (Field(line, "priceBreaks") is not { ValueKind: JsonValueKind.Array } breaks)
            {
                complete = false;
                continue;
            }
            foreach (var priceBreak in breaks.EnumerateArray())
            {
                var quantity = Number(priceBreak, "quantity");
                if (quantity is > 0) values.Add(quantity.Value);
                else complete = false;
                if (values.Count > MaximumCount)
                    throw new InvalidOperationException($"The quote exceeds the supported limit of {MaximumCount} quantity tiers per part.");
            }
        }
        return (values.Order().ToArray(), complete);
    }

    public static decimal[] ApplyConfirmation((decimal[] Quantities, bool Complete) quoted, decimal[] confirmed)
    {
        // Earlier confirmations stored only the endpoints. Keep verified Fulcrum tiers between matching endpoints.
        return confirmed.Length == 2 && quoted.Complete && quoted.Quantities.Length > 2
            && confirmed[0] == quoted.Quantities[0] && confirmed[^1] == quoted.Quantities[^1]
            ? quoted.Quantities : confirmed;
    }
}
