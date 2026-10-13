namespace EstimatingDashboard.Api.Dtos;

public sealed record MaterialRequirementsDto(int QuoteHistoryId, int QuoteNumber, DateTimeOffset CheckedAt,
    IReadOnlyList<MaterialQuotePartDto> Parts, IReadOnlyList<MaterialRequirementDto> Materials,
    IReadOnlyList<string> Warnings, int QuantityVersion = 0);
public sealed record MaterialQuotePartDto(string LineId, string ItemId, string PartNumber, string? Revision,
    IReadOnlyList<decimal> Quantities, bool QuantitiesOverridden = false);
public sealed record MaterialRequirementDto(string ItemId, string PartNumber, string Description, string UnitOfMeasure,
    decimal? RequiredMinimum, decimal? RequiredMaximum, decimal? AvailableQuantity,
    IReadOnlyList<decimal> QuoteQuantities, string Status, string? VendorId, string? VendorName,
    IReadOnlyList<string> VendorEmails, MaterialPurchaseOrderDto? RecentPurchaseOrder,
    IReadOnlyList<MaterialRequirementSourceDto> Sources, IReadOnlyList<string> Issues,
    int? RequestId = null, string? RequestStatus = null);
public sealed record MaterialPurchaseOrderDto(string Number, DateTimeOffset OrderedAt, decimal UnitPrice, string VendorName);
public sealed record MaterialRequirementSourceDto(string PartNumber, string ParentPartNumber,
    decimal? FixedQuantity, decimal? PerUnitQuantity, decimal? RequiredMinimum, decimal? RequiredMaximum);
