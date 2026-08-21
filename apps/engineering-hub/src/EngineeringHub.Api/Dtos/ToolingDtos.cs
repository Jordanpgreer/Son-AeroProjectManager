namespace EngineeringHub.Api.Dtos;

public sealed record ToolingDashboardDto(
    IReadOnlyList<ToolSummaryDto> Tools,
    int Total,
    int InStorage,
    int CheckedOut,
    int OutsideProcessing,
    int AuditOverdue);

public sealed record ToolSummaryDto(
    int Id,
    string ToolNumber,
    string Name,
    string ToolType,
    string Owner,
    bool IsArchived,
    string CustodyStatus,
    int? HomeLocationId,
    string? HomeLocation,
    int? CurrentLocationId,
    string? CurrentLocation,
    string? CurrentHolder,
    string? CurrentVendor,
    DateTime? CheckedOutAt,
    DateTime? LastAuditDate,
    IReadOnlyList<string> PartNumbers,
    int DocumentCount,
    string? Notes);

public sealed record ToolDetailDto(
    ToolSummaryDto Tool,
    string? Description,
    string CreatedBy,
    DateTime CreatedAt,
    string UpdatedBy,
    DateTime UpdatedAt,
    long Version,
    IReadOnlyList<ToolMovementDto> Movements,
    IReadOnlyList<ToolDocumentDto> Documents,
    IReadOnlyList<ToolAuditEntryDto> AuditHistory);

public sealed record ToolMovementDto(
    long Id,
    string Type,
    string? LocationCode,
    string? Vendor,
    string? Person,
    string? Purpose,
    bool? InspectionConfirmed,
    string? InspectionNotes,
    string SignedOffBy,
    DateTime RecordedAt);

public sealed record ToolDocumentDto(
    long Id,
    string Kind,
    string? DocumentNumber,
    string OriginalFileName,
    string FileType,
    long FileSize,
    string FileHash,
    string? Notes,
    DateTime DocumentDate,
    string UploadedBy,
    DateTime UploadedAt);

public sealed record ToolAuditEntryDto(long Id, string Action, string Details, string Actor, DateTime OccurredAt);

public sealed record ToolLocationDto(
    int Id,
    string Code,
    string? Description,
    bool IsActive,
    int ToolCount,
    int AssignedToolCount,
    string CreatedBy,
    DateTime CreatedAt);

public sealed record ToolUpsertDto(
    string ToolNumber,
    string Name,
    string ToolType,
    string Owner,
    string? Description,
    string? Notes,
    int? HomeLocationId,
    IReadOnlyList<string>? PartNumbers,
    bool IsArchived = false,
    long? Version = null);

public sealed record ToolCheckoutDto(
    string DestinationType,
    int? LocationId,
    string? Vendor,
    string Person,
    string? Purpose,
    bool InspectionConfirmed,
    string? InspectionNotes);

public sealed record ToolCheckinDto(int LocationId, string? Person, string? Purpose);
public sealed record ToolLocationCreateDto(string Code, string? Description);
public sealed record ToolLocationStatusDto(bool IsActive);
public sealed record ToolCatalogIssueDto(int Row, string? Column, string Message);

public sealed record ToolCatalogChangeDto(
    int Row,
    string RecordKey,
    string ChangeType,
    string Field,
    string? CurrentValue,
    string? UploadedValue);

public sealed record ToolCatalogValidationDto(
    string ReviewId,
    DateTimeOffset ExpiresAt,
    string FileName,
    int TotalRows,
    int NewRecords,
    int UpdatedRecords,
    int UnchangedRecords,
    int FieldChanges,
    int ErrorRows,
    IReadOnlyList<ToolCatalogIssueDto> Errors,
    IReadOnlyList<ToolCatalogChangeDto> Changes,
    string ReviewWorkbookUrl,
    bool CanApply);

public sealed record ToolCatalogApplyDto(bool ContinueWithErrors);
public sealed record ToolCatalogApplyResultDto(int Added, int Updated, int Skipped, int FieldChanges);
