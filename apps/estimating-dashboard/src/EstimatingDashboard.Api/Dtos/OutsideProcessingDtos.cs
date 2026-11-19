namespace EstimatingDashboard.Api.Dtos;

public sealed record OutsideProcessingRequirementsDto(int QuoteHistoryId, int QuoteNumber,
    DateTimeOffset CheckedAt, IReadOnlyList<OutsideProcessingOperationDto> Operations,
    IReadOnlyList<string> Warnings, string PlanningSource = PurchasingPlanningSources.Unknown);

public sealed record OutsideProcessingOperationDto(string OperationKey, string ItemId,
    string OperationId, string PartNumber, string Name, string? Instructions,
    decimal? RequiredMinimum, decimal? RequiredMaximum, IReadOnlyList<decimal> QuoteQuantities,
    string Status, string? VendorId, string? VendorName, IReadOnlyList<string> VendorEmails,
    IReadOnlyList<string> Issues, int? RequestId = null, string? RequestStatus = null,
    DateTimeOffset? SentAt = null, string? UnitOfMeasure = null);
