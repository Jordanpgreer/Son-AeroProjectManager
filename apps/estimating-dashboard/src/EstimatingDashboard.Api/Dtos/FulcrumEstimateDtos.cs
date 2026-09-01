using System.Text.Json;

namespace EstimatingDashboard.Api.Dtos;

public sealed record FulcrumEstimatePreviewDto(
    Guid ReviewId,
    DateTimeOffset ExpiresAt,
    string SourceFileName,
    string TargetSheet,
    string PartNumber,
    string Revision,
    string EstimateDate,
    string EstimatorInitials,
    int RateYear,
    IReadOnlyList<FulcrumOperationPreviewDto> Operations,
    IReadOnlyList<FulcrumMaterialPreviewDto> Materials,
    IReadOnlyList<FulcrumManualFieldDto> ManualFields,
    IReadOnlyList<FulcrumEstimateIssueDto> Issues,
    bool CanExport);

public sealed record FulcrumOperationPreviewDto(
    string Id,
    int SourceRow,
    string SourceOperation,
    int OperationNumber,
    string RateReferenceKey,
    string TargetOperation,
    decimal SuggestedSetupMinutes,
    decimal SuggestedRunMinutes,
    string? TimeType);

public sealed record FulcrumMaterialPreviewDto(
    string Id,
    int SourceRow,
    int TargetRow,
    string Description,
    decimal UnitsRequired);

public sealed record FulcrumManualFieldDto(
    string Id,
    string Label,
    string Description,
    string Sheet,
    string Cell,
    string Kind,
    bool Required,
    decimal? Min = null);

public sealed record FulcrumEstimateIssueDto(
    string Severity,
    string Sheet,
    int? Row,
    string? Column,
    string Message);

public sealed record FulcrumEstimateExportDto(
    IReadOnlyDictionary<string, JsonElement>? ManualValues,
    IReadOnlyList<FulcrumOperationOverrideDto>? OperationOverrides,
    FulcrumRateSnapshotDto? RateSnapshot);

public sealed record FulcrumOperationOverrideDto(
    string OperationId,
    decimal? SetupMinutes,
    decimal? RunMinutes);

public sealed record FulcrumRateSnapshotDto(
    int Year,
    IReadOnlyList<FulcrumOperationRateDto> OperationRates,
    FulcrumRateAssumptionsDto Assumptions);

public sealed record FulcrumOperationRateDto(
    string RateReferenceKey,
    string Operation,
    decimal Value);

public sealed record FulcrumRateAssumptionsDto(
    decimal Burden,
    decimal LaborGa,
    decimal MaterialGa,
    decimal ProcessGa,
    decimal LaborProfit,
    decimal MaterialProfit,
    decimal ProcessProfit);

public sealed record EstimatingRateReferenceDto(
    string Key,
    string Category,
    int SourceRow,
    string Operation);

public sealed record EstimatingOperationMappingDto(
    int Id,
    string FulcrumOperation,
    string RateReferenceKey,
    string EstimatingOperation,
    bool IsActive,
    int Version,
    DateTimeOffset UpdatedAt,
    string UpdatedBy);

public sealed record EstimatingOperationMappingCatalogDto(
    IReadOnlyList<EstimatingRateReferenceDto> RateReferences,
    IReadOnlyList<EstimatingOperationMappingDto> Rules);

public sealed record CreateEstimatingOperationMappingDto(
    string FulcrumOperation,
    string RateReferenceKey);

public sealed record UpdateEstimatingOperationMappingDto(
    string FulcrumOperation,
    string RateReferenceKey,
    int Version);

public sealed record DeactivateEstimatingOperationMappingDto(int Version);
