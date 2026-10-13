using System.Globalization;
using System.Net.Mail;
using System.Text.Json;
using EstimatingDashboard.Api.Dtos;

namespace EstimatingDashboard.Api.Services;

internal static class FulcrumMaterialRequirementsPolicy
{
    // Verified against the current ITAR public OpenAPI schema on 2026-10-12.
    // fixedUnits is an optional nullable per-lot amount, additional to valueTypeUnits.
    public static (decimal? Fixed, decimal? PerUnit) Usage(JsonElement input)
    {
        var fixedField = Field(input, "fixedUnits");
        decimal? fixedUnits = fixedField.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined
            ? 0 : Number(input, "fixedUnits");
        var units = Number(input, "valueTypeUnits");
        decimal? perUnit = Text(input, "valueType") switch
        {
            "requires" when units >= 0 => units,
            "creates" when units > 0 => 1 / units,
            _ => null
        };
        return (fixedUnits >= 0 ? fixedUnits : null, perUnit);
    }

    public static decimal? Required(decimal? fixedQuantity, decimal? perUnit, decimal? parentQuantity) =>
        fixedQuantity >= 0 && perUnit >= 0 && parentQuantity > 0
            ? checked(fixedQuantity + perUnit * parentQuantity) : null;

    public static IReadOnlyList<decimal> QuoteQuantities(decimal? minimum, decimal? maximum, bool hasAlternates)
    {
        if (minimum is not > 0 || maximum is not > 0) return [];
        return new[] { minimum.Value, hasAlternates ? maximum.Value : checked(maximum.Value * 1.5m) }
            .Distinct().Order().ToArray();
    }

    public static MaterialPurchaseOrderDto? RecentPurchase(JsonElement history, DateTimeOffset now)
    {
        return Array(history, "purchases").Where(purchase =>
                Text(purchase, "purchaseOrderStatus").ToLowerInvariant() is "ordered" or "paid")
            .Select(purchase => new { Purchase = purchase, Date = Date(purchase, "orderDateUtc"), Price = Number(purchase, "effectiveUnitPrice") })
            .Where(row => row.Date.HasValue && row.Date.Value.UtcDateTime.Date >= now.AddMonths(-3).UtcDateTime.Date
                && row.Date <= now && row.Price > 0 && Number(row.Purchase, "quantityOrdered") > 0
                && Number(row.Purchase, "purchaseOrderNumber") > 0)
            .OrderByDescending(row => row.Date).Select(row => new MaterialPurchaseOrderDto(
                Text(row.Purchase, "purchaseOrderNumber"), row.Date!.Value, row.Price!.Value,
                Text(Field(row.Purchase, "vendor"), "name"))).FirstOrDefault();
    }

    public static bool HasCompleteHistory(JsonElement history) =>
        Field(history, "purchases").ValueKind == JsonValueKind.Array
        && Number(history, "windowMonths") >= 3
        && Number(history, "totalPurchaseCount") == Field(history, "purchases").GetArrayLength();

    public static bool ValidEmail(string value) => value.Length is > 0 and <= 254
        && !value.Any(char.IsControl) && !value.Contains(';') && !value.Contains(',')
        && MailAddress.TryCreate(value, out var address) && address.Address.Equals(value, StringComparison.OrdinalIgnoreCase)
        && address.Host.Contains('.');

    public static JsonElement Field(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) ? value : default;
    public static string Text(JsonElement element, string property) => Field(element, property) is var value
        && value.ValueKind is JsonValueKind.String or JsonValueKind.Number ? value.ToString().Trim() : "";
    public static decimal? Number(JsonElement element, string property) => Field(element, property) is var value
        && value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var number) ? number : null;
    public static bool Boolean(JsonElement element, string property) => Field(element, property).ValueKind == JsonValueKind.True;
    public static IReadOnlyList<JsonElement> Array(JsonElement element, string property) => Field(element, property) is var value
        && value.ValueKind == JsonValueKind.Array ? value.EnumerateArray().ToArray() : [];
    private static DateTimeOffset? Date(JsonElement element, string property) =>
        DateTimeOffset.TryParse(Text(element, property), CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal, out var date) ? date : null;
}
