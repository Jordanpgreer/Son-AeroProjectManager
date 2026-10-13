namespace EstimatingDashboard.Api.Models;

public sealed class MaterialPurchasingState
{
    public int QuoteHistoryId { get; set; }
    public EstimatingQuoteHistoryRecord QuoteHistory { get; set; } = null!;
    public string QuantitiesJson { get; set; } = "[]";
    public string DraftsJson { get; set; } = "[]";
    public int Version { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public string UpdatedBy { get; set; } = "";
}
