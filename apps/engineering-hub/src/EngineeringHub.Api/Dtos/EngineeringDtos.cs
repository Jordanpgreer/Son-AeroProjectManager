namespace EngineeringHub.Api.Dtos;

public sealed record MeDto(string AccountName, string DisplayName, string Role);

public sealed record EngineeringModuleDto(
    string Id,
    string Name,
    string Summary,
    string AccessNotice,
    IReadOnlyList<EngineeringSectionDto> Sections);

public sealed record EngineeringSectionDto(
    string Id,
    string Title,
    string Summary,
    string Status,
    IReadOnlyList<string> Highlights);

public sealed record EngineeringDashboardDto(
    string SearchHint,
    IReadOnlyList<EngineeringSearchCategoryDto> Categories,
    IReadOnlyList<EngineeringSearchResultDto> Results,
    EngineeringOperationalSummaryDto Summary,
    IReadOnlyList<string> Customers);

public sealed record EngineeringOperationalSummaryDto(
    int TotalDrawings,
    int DraftDrawings,
    int ReviewQueue,
    int ApprovedDrawings,
    int CheckedOutMylars);

public sealed record EngineeringSearchCategoryDto(
    string Id,
    string Title,
    int Count);

public sealed record EngineeringSearchResultDto(
    string Id,
    string Category,
    string CategoryLabel,
    string Title,
    string Identifier,
    string Subtitle,
    string? Customer,
    string? SpecificationNumber,
    string? WorkOrder,
    string? ReportNumber,
    IReadOnlyList<string> Tags,
    string Note,
    int? DrawingId = null,
    IReadOnlyList<string>? AttentionReasons = null);

public sealed record ErrorDto(string Code, string Message);
